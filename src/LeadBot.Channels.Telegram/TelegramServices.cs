using System.Text.Json;
using LeadBot.Application.Abstractions;
using LeadBot.Domain.Leads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadBot.Channels.Telegram;

public sealed class TelegramChannelSender(TelegramApiClient api) : IChannelSender
{
    private const int MaxLength = 4000; // лимит Telegram — 4096

    public ChannelKind Channel => ChannelKind.Telegram;

    public async Task SendTextAsync(ChannelIdentity to, string text, CancellationToken ct)
    {
        var chatId = long.Parse(to.ChatId);
        foreach (var chunk in Split(text, MaxLength))
            await api.SendMessageAsync(chatId, chunk, to.BusinessConnectionId, ct: ct);
    }

    public Task SendTypingAsync(ChannelIdentity to, CancellationToken ct) =>
        api.SendChatActionAsync(long.Parse(to.ChatId), "typing", to.BusinessConnectionId, ct);

    internal static IEnumerable<string> Split(string text, int max)
    {
        while (text.Length > max)
        {
            var cut = text.LastIndexOf('\n', max);
            if (cut < max / 2) cut = text.LastIndexOf(' ', max);
            if (cut < max / 2) cut = max;
            yield return text[..cut];
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }
}

/// <summary>Уведомления владельцу в Telegram с кнопками управления диалогом.</summary>
public sealed class TelegramOwnerNotifier(
    TelegramApiClient api,
    IOptions<TelegramOptions> options,
    ILogger<TelegramOwnerNotifier> log) : IOwnerNotifier
{
    public async Task NotifyAsync(Lead lead, string text, bool urgent, CancellationToken ct)
    {
        if (options.Value.OwnerChatId is not { } ownerChat) return;

        var p = lead.Profile;
        var who = p.Name ?? lead.Identity.DisplayName ?? "клиент";
        var username = lead.Identity.Username is { } u ? $" @{u}" : "";
        var body = $"""
            {(urgent ? "🔥 " : "")}{text}

            👤 {who}{username} · {lead.Identity.Channel}{(lead.Identity.BusinessConnectionId is null ? "" : " (Business)")}
            📍 Стадия: {lead.Stage}
            🧩 {p.ServiceType}: {p.ProblemSummary ?? "задача не описана"}
            💰 {p.BudgetRange ?? "—"} · ⏱ {p.Timeline ?? "—"}
            """;

        try
        {
            await api.SendMessageAsync(ownerChat, body, keyboard:
            [[
                new InlineButton("▶️ Вернуть боту", CallbackData: $"lead:bot:{lead.Id}"),
                new InlineButton("✋ Забрать себе", CallbackData: $"lead:human:{lead.Id}")
            ]], ct: ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось уведомить владельца");
        }
    }
}

/// <summary>Регистрирует вебхук при старте, если задан PublicBaseUrl.</summary>
public sealed class TelegramWebhookRegistrar(
    IServiceScopeFactory scopes,
    IOptions<TelegramOptions> options,
    ILogger<TelegramWebhookRegistrar> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled || !o.RegisterWebhookOnStartup || string.IsNullOrWhiteSpace(o.PublicBaseUrl)) return;
        try
        {
            using var scope = scopes.CreateScope();
            var api = scope.ServiceProvider.GetRequiredService<TelegramApiClient>();
            await api.SetWebhookAsync($"{o.PublicBaseUrl.TrimEnd('/')}/webhooks/telegram", o.WebhookSecret, ct);
            log.LogInformation("Telegram webhook зарегистрирован");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось зарегистрировать Telegram webhook");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public static class TelegramExtensions
{
    public static IServiceCollection AddTelegramChannel(this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(TelegramOptions.Section);
        services.Configure<TelegramOptions>(section);
        if (!section.GetValue("Enabled", true)) return services;

        services.AddHttpClient<TelegramApiClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<BusinessConnectionStore>();
        services.AddSingleton<GroupQuestionCache>();
        services.AddScoped<TelegramUpdateHandler>();
        services.AddScoped<IChannelSender, TelegramChannelSender>();
        if (section.GetValue<long?>("OwnerChatId") is not null)
            services.AddScoped<IOwnerNotifier, TelegramOwnerNotifier>();
        services.AddHostedService<TelegramWebhookRegistrar>();
        return services;
    }

    public static IEndpointRouteBuilder MapTelegramWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/telegram", async (HttpRequest request, TelegramUpdateHandler handler,
            IOptions<TelegramOptions> options, ILogger<TelegramUpdateHandler> log, CancellationToken ct) =>
        {
            if (!string.Equals(request.Headers["X-Telegram-Bot-Api-Secret-Token"], options.Value.WebhookSecret, StringComparison.Ordinal))
                return Results.Unauthorized();

            var update = await JsonSerializer.DeserializeAsync<TgUpdate>(request.Body, TelegramApiClient.Json, ct);
            if (update is null) return Results.BadRequest();

            try
            {
                await handler.HandleAsync(update, ct);
            }
            catch (Exception ex)
            {
                // Отвечаем 200, иначе Telegram будет бесконечно повторять апдейт.
                log.LogError(ex, "Ошибка обработки Telegram update {UpdateId}", update.UpdateId);
            }
            return Results.Ok();
        });
        return app;
    }
}
