using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LeadBot.Application.Abstractions;
using LeadBot.Application.Messaging;
using LeadBot.Domain.Leads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadBot.Channels.Max;

// ВАЖНО: схема ниже соответствует MAX Bot API (platform-api.max.ru, наследник TamTam Bot API) на момент написания.
// Перед запуском сверьте поля с https://dev.max.ru/docs-api — API молодой и меняется.

public sealed class MaxOptions
{
    public const string Section = "Max";

    public bool Enabled { get; set; } = true;
    public string BotToken { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://platform-api.max.ru";
    /// <summary>Секрет подписки — MAX присылает его в заголовке X-Max-Bot-Api-Secret.</summary>
    public string WebhookSecret { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    public bool RegisterWebhookOnStartup { get; set; } = true;
    /// <summary>Username бота без @ — для распознавания упоминаний в групповых чатах.</summary>
    public string BotUsername { get; set; } = "";
    /// <summary>Ссылка на бота для кнопки «написать в личку», например https://max.ru/your_bot</summary>
    public string BotLink { get; set; } = "";
    public string GroupReplyText { get; set; } = "Спасибо за вопрос! Напишите нам в личные сообщения — там подробно ответим 👇";
}

// ---------- DTO ----------

public sealed class MaxUpdate
{
    /// <summary>message_created | bot_started | message_callback | ...</summary>
    public string UpdateType { get; init; } = "";
    public long Timestamp { get; init; }
    public MaxMessage? Message { get; init; }
    // bot_started:
    public long? ChatId { get; init; }
    public MaxUser? User { get; init; }
    public string? Payload { get; init; }
}

public sealed class MaxMessage
{
    public MaxUser? Sender { get; init; }
    public MaxRecipient Recipient { get; init; } = new();
    public MaxMessageBody Body { get; init; } = new();
    public long Timestamp { get; init; }
}

public sealed class MaxRecipient
{
    public long? ChatId { get; init; }
    /// <summary>dialog — личка с ботом; chat — групповой чат; channel — канал.</summary>
    public string ChatType { get; init; } = "dialog";
    public long? UserId { get; init; }
}

public sealed class MaxMessageBody
{
    public string Mid { get; init; } = "";
    public string? Text { get; init; }
    public JsonElement? Attachments { get; init; }
}

public sealed class MaxUser
{
    public long UserId { get; init; }
    public string? Name { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Username { get; init; }
    public bool IsBot { get; init; }
    public string? DisplayName => Name ?? string.Join(' ', new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

// ---------- Client ----------

public sealed class MaxApiClient(HttpClient http, IOptions<MaxOptions> options)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private HttpRequestMessage Request(HttpMethod method, string path, object? body)
    {
        var o = options.Value;
        var req = new HttpRequestMessage(method, $"{o.ApiBaseUrl.TrimEnd('/')}{path}");
        // Токен передаётся в заголовке Authorization (query-параметр access_token больше не поддерживается).
        req.Headers.TryAddWithoutValidation("Authorization", o.BotToken);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        return req;
    }

    private async Task SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        using (var resp = await http.SendAsync(req, ct))
        {
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"MAX {req.Method} {req.RequestUri?.AbsolutePath}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        }
    }

    public Task SendMessageAsync(long chatId, string text, (string Text, string Url)? linkButton = null, CancellationToken ct = default) =>
        SendAsync(Request(HttpMethod.Post, $"/messages?chat_id={chatId}", new
        {
            text,
            attachments = linkButton is { } b
                ? new object[]
                {
                    new { type = "inline_keyboard", payload = new { buttons = new[] { new[] { new { type = "link", text = b.Text, url = b.Url } } } } }
                }
                : null
        }), ct);

    public Task SendTypingAsync(long chatId, CancellationToken ct) =>
        SendAsync(Request(HttpMethod.Post, $"/chats/{chatId}/actions", new { action = "typing_on" }), ct);

    public Task SubscribeAsync(string url, string secret, CancellationToken ct) =>
        SendAsync(Request(HttpMethod.Post, "/subscriptions", new
        {
            url,
            update_types = new[] { "message_created", "bot_started" },
            secret
        }), ct);
}

// ---------- Handler ----------

public sealed class MaxUpdateHandler(
    IncomingMessageQueue queue,
    MaxApiClient api,
    IOptions<MaxOptions> options)
{
    public async Task HandleAsync(MaxUpdate update, CancellationToken ct)
    {
        switch (update.UpdateType)
        {
            case "bot_started" when update.User is { } user && update.ChatId is { } chatId:
            {
                var identity = new ChannelIdentity(ChannelKind.Max, user.UserId.ToString(), chatId.ToString(), user.DisplayName, user.Username);
                await queue.EnqueueAsync(new IncomingMessage(identity, IncomingKind.Start, "/start",
                    $"maxstart:{chatId}:{update.Timestamp}", FromMs(update.Timestamp), update.Payload), ct);
                break;
            }
            case "message_created" when update.Message is { Sender: { IsBot: false } sender } m:
            {
                var text = !string.IsNullOrWhiteSpace(m.Body.Text) ? m.Body.Text
                    : m.Body.Attachments is not null ? "[клиент прислал вложение]" : null;
                if (text is null || m.Recipient.ChatId is not { } chatId) return;

                if (m.Recipient.ChatType == "dialog")
                {
                    var identity = new ChannelIdentity(ChannelKind.Max, sender.UserId.ToString(), chatId.ToString(), sender.DisplayName, sender.Username);
                    await queue.EnqueueAsync(new IncomingMessage(identity, IncomingKind.ClientText, text,
                        $"max:{m.Body.Mid}", FromMs(m.Timestamp)), ct);
                }
                else if (m.Recipient.ChatType == "chat")
                {
                    await HandleGroupAsync(chatId, text, ct);
                }
                break;
            }
        }
    }

    private async Task HandleGroupAsync(long chatId, string text, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.BotUsername) || string.IsNullOrWhiteSpace(o.BotLink)) return;
        if (!text.Contains("@" + o.BotUsername, StringComparison.OrdinalIgnoreCase)) return;

        await api.SendMessageAsync(chatId, o.GroupReplyText, ("Написать в личку", o.BotLink), ct);
    }

    private static DateTimeOffset FromMs(long ms) =>
        ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UtcNow;
}

public sealed class MaxChannelSender(MaxApiClient api) : IChannelSender
{
    private const int MaxLength = 4000;
    public ChannelKind Channel => ChannelKind.Max;

    public async Task SendTextAsync(ChannelIdentity to, string text, CancellationToken ct)
    {
        var chatId = long.Parse(to.ChatId);
        for (var i = 0; i < text.Length; i += MaxLength)
            await api.SendMessageAsync(chatId, text.Substring(i, Math.Min(MaxLength, text.Length - i)), ct: ct);
    }

    public Task SendTypingAsync(ChannelIdentity to, CancellationToken ct) =>
        api.SendTypingAsync(long.Parse(to.ChatId), ct);
}

public sealed class MaxWebhookRegistrar(IServiceScopeFactory scopes, IOptions<MaxOptions> options, ILogger<MaxWebhookRegistrar> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled || !o.RegisterWebhookOnStartup || string.IsNullOrWhiteSpace(o.PublicBaseUrl)) return;
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MaxApiClient>()
                .SubscribeAsync($"{o.PublicBaseUrl.TrimEnd('/')}/webhooks/max", o.WebhookSecret, ct);
            log.LogInformation("MAX webhook зарегистрирован");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось зарегистрировать MAX webhook");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public static class MaxExtensions
{
    public static IServiceCollection AddMaxChannel(this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(MaxOptions.Section);
        services.Configure<MaxOptions>(section);
        if (!section.GetValue("Enabled", true)) return services;

        services.AddHttpClient<MaxApiClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<MaxUpdateHandler>();
        services.AddScoped<IChannelSender, MaxChannelSender>();
        services.AddHostedService<MaxWebhookRegistrar>();
        return services;
    }

    public static IEndpointRouteBuilder MapMaxWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/max", async (HttpRequest request, MaxUpdateHandler handler,
            IOptions<MaxOptions> options, ILogger<MaxUpdateHandler> log, CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(options.Value.WebhookSecret) &&
                !string.Equals(request.Headers["X-Max-Bot-Api-Secret"], options.Value.WebhookSecret, StringComparison.Ordinal))
                return Results.Unauthorized();

            var update = await JsonSerializer.DeserializeAsync<MaxUpdate>(request.Body, MaxApiClient.Json, ct);
            if (update is null) return Results.BadRequest();

            try { await handler.HandleAsync(update, ct); }
            catch (Exception ex) { log.LogError(ex, "Ошибка обработки MAX update {Type}", update.UpdateType); }
            return Results.Ok();
        });
        return app;
    }
}
