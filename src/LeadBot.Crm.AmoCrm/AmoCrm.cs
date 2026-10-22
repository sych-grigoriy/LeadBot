using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LeadBot.Application.Conversation;
using LeadBot.Application.Crm;
using LeadBot.Domain.Leads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadBot.Crm.AmoCrm;

public sealed class AmoCrmOptions
{
    public const string Section = "AmoCrm";

    public bool Enabled { get; set; } = true;
    /// <summary>Поддомен аккаунта: {subdomain}.amocrm.ru</summary>
    public string Subdomain { get; set; } = "";
    public string Domain { get; set; } = "amocrm.ru";

    /// <summary>Вариант 1 (проще): долгосрочный токен приватной интеграции.</summary>
    public string? LongLivedToken { get; set; }

    /// <summary>Вариант 2: OAuth 2.0 с ротацией refresh-токена.</summary>
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RedirectUri { get; set; }
    /// <summary>Первый refresh-токен (получить через обмен authorization code). Дальше хранится в TokenFile.</summary>
    public string? InitialRefreshToken { get; set; }
    public string TokenFile { get; set; } = "data/amocrm-tokens.json";

    public long PipelineId { get; set; }
    public long? ResponsibleUserId { get; set; }

    /// <summary>Стадия воронки бота → status_id в amoCRM. Won=142 и Lost=143 — системные статусы amoCRM.</summary>
    public Dictionary<string, long> StatusMap { get; set; } = new() { ["Won"] = 142, ["Lost"] = 143 };

    /// <summary>Поля сделки: Budget, Timeline, ServiceType, ProblemSummary, Source, Channel, CallSlot → field_id.</summary>
    public Dictionary<string, long> LeadFields { get; set; } = new();

    /// <summary>Поле контакта для username в мессенджере (необязательно).</summary>
    public long? ContactMessengerFieldId { get; set; }

    public string[] Tags { get; set; } = ["бот"];

    /// <summary>Секрет в URL вебхука amoCRM: /webhooks/amocrm/{secret}</summary>
    public string WebhookSecret { get; set; } = "";

    public string BaseUrl => $"https://{Subdomain}.{Domain}";
}

// ---------- Токены ----------

/// <summary>
/// Access-токен для amoCRM. Долгосрочный токен — просто возвращается.
/// OAuth: access живёт ~24 ч, refresh одноразовый и ротируется при каждом обновлении — поэтому храним его на диске.
/// </summary>
public sealed class AmoTokenProvider(IHttpClientFactory httpFactory, IOptions<AmoCrmOptions> options, ILogger<AmoTokenProvider> log)
{
    private sealed record TokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TokenSet? _cached;

    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.LongLivedToken))
            return o.LongLivedToken;

        await _gate.WaitAsync(ct);
        try
        {
            _cached ??= await LoadAsync(ct);
            if (!forceRefresh && _cached is { } t && t.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
                return t.AccessToken;

            var refresh = _cached?.RefreshToken ?? o.InitialRefreshToken
                ?? throw new InvalidOperationException("amoCRM: нет refresh-токена. Задайте AmoCrm:InitialRefreshToken или LongLivedToken.");

            using var http = httpFactory.CreateClient();
            using var resp = await http.PostAsJsonAsync($"{o.BaseUrl}/oauth2/access_token", new
            {
                client_id = o.ClientId,
                client_secret = o.ClientSecret,
                grant_type = "refresh_token",
                refresh_token = refresh,
                redirect_uri = o.RedirectUri
            }, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new InvalidOperationException("пустой ответ OAuth");

            _cached = new TokenSet(
                json["access_token"]!.GetValue<string>(),
                json["refresh_token"]!.GetValue<string>(),
                DateTimeOffset.UtcNow.AddSeconds(json["expires_in"]!.GetValue<int>()));
            await SaveAsync(_cached, ct);
            log.LogInformation("amoCRM: access-токен обновлён");
            return _cached.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TokenSet?> LoadAsync(CancellationToken ct)
    {
        var path = options.Value.TokenFile;
        if (!File.Exists(path)) return null;
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TokenSet>(fs, cancellationToken: ct);
    }

    private async Task SaveAsync(TokenSet tokens, CancellationToken ct)
    {
        var path = options.Value.TokenFile;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(tokens), ct);
        File.Move(tmp, path, overwrite: true);
    }
}

// ---------- HTTP-клиент API v4 ----------

public sealed class AmoCrmClient(HttpClient http, AmoTokenProvider tokens, IOptions<AmoCrmOptions> options)
{
    // Лимит amoCRM — 7 запросов в секунду на интеграцию.
    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(160);

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await WaitThrottleAsync(ct);
            using var req = new HttpRequestMessage(method, options.Value.BaseUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(attempt > 0, ct));
            if (body is not null) req.Content = JsonContent.Create(body);

            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && string.IsNullOrEmpty(options.Value.LongLivedToken))
                continue; // протух access — обновим и повторим

            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"amoCRM {method} {path}: {(int)resp.StatusCode} {text}", null, resp.StatusCode);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        throw new HttpRequestException("amoCRM: 401 после обновления токена");
    }

    private static async Task WaitThrottleAsync(CancellationToken ct)
    {
        await Throttle.WaitAsync(ct);
        try
        {
            var wait = _lastCall + MinInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastCall = DateTimeOffset.UtcNow;
        }
        finally { Throttle.Release(); }
    }
}

// ---------- Реализация порта CRM ----------

public sealed class AmoCrmGateway(AmoCrmClient amo, IOptions<AmoCrmOptions> options) : ICrmGateway
{
    private AmoCrmOptions Opt => options.Value;

    public async Task<(long LeadId, long? ContactId)> UpsertLeadAsync(Lead lead, CancellationToken ct)
    {
        var statusId = Opt.StatusMap.TryGetValue(lead.Stage.ToString(), out var s) && s > 0 ? s : (long?)null;

        if (lead.CrmLeadId is null)
        {
            var payload = new JsonArray(new JsonObject
            {
                ["name"] = LeadName(lead),
                ["pipeline_id"] = Opt.PipelineId,
                ["status_id"] = statusId,
                ["responsible_user_id"] = Opt.ResponsibleUserId,
                ["custom_fields_values"] = LeadFields(lead),
                ["_embedded"] = new JsonObject
                {
                    ["contacts"] = new JsonArray(Contact(lead)),
                    ["tags"] = new JsonArray(Tags(lead).Select(t => (JsonNode)new JsonObject { ["name"] = t }).ToArray())
                }
            });
            RemoveNulls(payload);

            var result = await amo.SendAsync(HttpMethod.Post, "/api/v4/leads/complex", payload, ct);
            var first = result!.AsArray()[0]!;
            return (first["id"]!.GetValue<long>(), first["contact_id"]?.GetValue<long>());
        }

        var leadId = lead.CrmLeadId.Value;
        var update = new JsonObject
        {
            ["name"] = LeadName(lead),
            ["custom_fields_values"] = LeadFields(lead)
        };

        // Статус двигаем, только если менеджер не увёл сделку в «свой» этап, о котором бот не знает.
        if (statusId is not null)
        {
            var current = await amo.SendAsync(HttpMethod.Get, $"/api/v4/leads/{leadId}", null, ct);
            var currentStatus = current?["status_id"]?.GetValue<long>();
            if (currentStatus is { } cs && cs != statusId && Opt.StatusMap.Values.Any(v => v > 0 && v == cs))
            {
                update["status_id"] = statusId;
                update["pipeline_id"] = Opt.PipelineId;
            }
        }
        RemoveNulls(update);
        await amo.SendAsync(HttpMethod.Patch, $"/api/v4/leads/{leadId}", update, ct);

        if (lead.CrmContactId is { } contactId)
        {
            var contact = Contact(lead);
            RemoveNulls(contact);
            await amo.SendAsync(HttpMethod.Patch, $"/api/v4/contacts/{contactId}", contact, ct);
        }

        return (leadId, lead.CrmContactId);
    }

    public Task AddNoteAsync(long crmLeadId, string text, CancellationToken ct) =>
        amo.SendAsync(HttpMethod.Post, $"/api/v4/leads/{crmLeadId}/notes", new[]
        {
            new { note_type = "common", @params = new { text = Truncate(text, 10_000) } }
        }, ct);

    public Task CreateTaskAsync(long crmLeadId, string text, DateTimeOffset dueAt, CancellationToken ct) =>
        amo.SendAsync(HttpMethod.Post, "/api/v4/tasks", new[]
        {
            new
            {
                text,
                complete_till = dueAt.ToUnixTimeSeconds(),
                entity_id = crmLeadId,
                entity_type = "leads",
                responsible_user_id = Opt.ResponsibleUserId
            }
        }, ct);

    private static string LeadName(Lead lead)
    {
        var who = lead.Profile.Name ?? lead.Identity.DisplayName ?? "Клиент";
        var what = lead.Profile.ServiceType switch
        {
            ServiceType.CustomDevelopment => "разработка",
            ServiceType.AiIntegration => "внедрение ИИ",
            ServiceType.Consulting => "консалтинг",
            ServiceType.Support => "поддержка",
            _ => "заявка"
        };
        return $"{who} — {what} ({lead.Identity.Channel})";
    }

    private JsonArray? LeadFields(Lead lead)
    {
        var p = lead.Profile;
        var values = new Dictionary<string, string?>
        {
            ["Budget"] = p.BudgetRange,
            ["Timeline"] = p.Timeline,
            ["ServiceType"] = p.ServiceType == ServiceType.Unknown ? null : StageNames.ServiceName(p.ServiceType),
            ["ProblemSummary"] = p.ProblemSummary,
            ["Source"] = p.Source,
            ["Channel"] = lead.Identity.BusinessConnectionId is null ? lead.Identity.Channel.ToString() : "Telegram Business",
            ["CallSlot"] = p.CallSlot?.ToString("dd.MM.yyyy HH:mm zzz"),
        };

        var arr = new JsonArray();
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !Opt.LeadFields.TryGetValue(key, out var fieldId) || fieldId <= 0) continue;
            arr.Add(new JsonObject
            {
                ["field_id"] = fieldId,
                ["values"] = new JsonArray(new JsonObject { ["value"] = value })
            });
        }
        return arr.Count == 0 ? null : arr;
    }

    private JsonObject Contact(Lead lead)
    {
        var p = lead.Profile;
        var fields = new JsonArray();
        if (!string.IsNullOrWhiteSpace(p.Phone))
            fields.Add(new JsonObject { ["field_code"] = "PHONE", ["values"] = new JsonArray(new JsonObject { ["value"] = p.Phone, ["enum_code"] = "WORK" }) });
        if (!string.IsNullOrWhiteSpace(p.Email))
            fields.Add(new JsonObject { ["field_code"] = "EMAIL", ["values"] = new JsonArray(new JsonObject { ["value"] = p.Email, ["enum_code"] = "WORK" }) });
        if (Opt.ContactMessengerFieldId is { } mf && lead.Identity.Username is { } u)
            fields.Add(new JsonObject { ["field_id"] = mf, ["values"] = new JsonArray(new JsonObject { ["value"] = $"@{u} ({lead.Identity.Channel})" }) });

        return new JsonObject
        {
            ["name"] = p.Name ?? lead.Identity.DisplayName ?? "Клиент",
            ["custom_fields_values"] = fields.Count == 0 ? null : fields
        };
    }

    private IEnumerable<string> Tags(Lead lead) =>
        Opt.Tags.Append(lead.Identity.BusinessConnectionId is null ? lead.Identity.Channel.ToString() : "Telegram Business");

    private static void RemoveNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
                    obj.Remove(key);
                foreach (var kv in obj) RemoveNulls(kv.Value);
                break;
            case JsonArray arr:
                foreach (var item in arr) RemoveNulls(item);
                break;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

// ---------- Вебхук из amoCRM (смена этапа менеджером) ----------

public static partial class AmoCrmExtensions
{
    public static IServiceCollection AddAmoCrm(this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(AmoCrmOptions.Section);
        services.Configure<AmoCrmOptions>(section);
        if (!section.GetValue("Enabled", true)) return services;

        services.AddHttpClient();
        services.AddSingleton<AmoTokenProvider>();
        services.AddHttpClient<AmoCrmClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<ICrmGateway, AmoCrmGateway>();
        return services;
    }

    [GeneratedRegex(@"^leads\[(status|update)\]\[(\d+)\]\[(id|status_id|pipeline_id)\]$")]
    private static partial Regex LeadStatusKey();

    /// <summary>
    /// В amoCRM: Настройки → Интеграции → Web hooks → URL https://.../webhooks/amocrm/{secret},
    /// событие «Сделка: смена статуса». amoCRM шлёт form-urlencoded вида leads[status][0][id]=...
    /// </summary>
    public static IEndpointRouteBuilder MapAmoCrmWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/amocrm/{secret}", async (string secret, HttpRequest request,
            ConversationOrchestrator orchestrator, IOptions<AmoCrmOptions> options,
            ILogger<AmoCrmGateway> log, CancellationToken ct) =>
        {
            var o = options.Value;
            if (string.IsNullOrEmpty(o.WebhookSecret) || !string.Equals(secret, o.WebhookSecret, StringComparison.Ordinal))
                return Results.Unauthorized();
            if (!request.HasFormContentType) return Results.BadRequest();

            var form = await request.ReadFormAsync(ct);
            var items = new Dictionary<string, Dictionary<string, string>>();
            foreach (var (key, value) in form)
            {
                var m = LeadStatusKey().Match(key);
                if (!m.Success) continue;
                var bucket = items.TryGetValue(m.Groups[1].Value + m.Groups[2].Value, out var b) ? b : items[m.Groups[1].Value + m.Groups[2].Value] = new();
                bucket[m.Groups[3].Value] = value.ToString();
            }

            var reverse = o.StatusMap.Where(kv => kv.Value > 0).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
            foreach (var item in items.Values)
            {
                if (!item.TryGetValue("id", out var idRaw) || !long.TryParse(idRaw, out var crmLeadId)) continue;
                if (!item.TryGetValue("status_id", out var stRaw) || !long.TryParse(stRaw, out var statusId)) continue;
                if (!reverse.TryGetValue(statusId, out var stageName) || !Enum.TryParse<LeadStage>(stageName, out var stage)) continue;

                try { await orchestrator.ApplyCrmStageAsync(crmLeadId, stage, ct); }
                catch (Exception ex) { log.LogError(ex, "Не удалось применить статус amoCRM для сделки {Id}", crmLeadId); }
            }
            return Results.Ok();
        });
        return app;
    }
}
