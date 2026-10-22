using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace LeadBot.Channels.Telegram;

public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    public bool Enabled { get; set; } = true;
    public string BotToken { get; set; } = "";
    /// <summary>Username бота без @ — для deep-link и распознавания упоминаний в группах.</summary>
    public string BotUsername { get; set; } = "";
    /// <summary>Секрет, который Telegram пришлёт в заголовке X-Telegram-Bot-Api-Secret-Token.</summary>
    public string WebhookSecret { get; set; } = "";
    /// <summary>Публичный HTTPS-адрес сервиса, например https://bot.example.ru</summary>
    public string PublicBaseUrl { get; set; } = "";
    public bool RegisterWebhookOnStartup { get; set; } = true;
    /// <summary>Чат владельца с ботом (ваш user id) — сюда летят уведомления. Сначала напишите боту /start.</summary>
    public long? OwnerChatId { get; set; }
    /// <summary>Ключевые слова, на которые бот реагирует в группах (кроме прямых упоминаний и ответов боту).</summary>
    public string[] GroupKeywords { get; set; } = [];
    public string GroupReplyText { get; set; } =
        "Спасибо за вопрос! Давайте обсудим детали в личных сообщениях — нажмите кнопку ниже 👇";
}

// ---------- DTO Bot API (только нужные поля) ----------

public sealed class TgUpdate
{
    public long UpdateId { get; init; }
    public TgMessage? Message { get; init; }
    public TgBusinessConnection? BusinessConnection { get; init; }
    public TgMessage? BusinessMessage { get; init; }
    public TgCallbackQuery? CallbackQuery { get; init; }
}

public sealed class TgMessage
{
    public long MessageId { get; init; }
    public TgUser? From { get; init; }
    public TgChat Chat { get; init; } = new();
    public long Date { get; init; }
    public string? Text { get; init; }
    public string? Caption { get; init; }
    public string? BusinessConnectionId { get; init; }
    /// <summary>Заполнено, если сообщение в бизнес-чате отправил бот — это наше же эхо.</summary>
    public TgUser? SenderBusinessBot { get; init; }
    public TgMessage? ReplyToMessage { get; init; }
    public JsonElement? Photo { get; init; }
    public JsonElement? Document { get; init; }
    public JsonElement? Voice { get; init; }
    public JsonElement? VideoNote { get; init; }
    public JsonElement? Sticker { get; init; }
}

public sealed class TgUser
{
    public long Id { get; init; }
    public bool IsBot { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Username { get; init; }
    public string DisplayName => string.Join(' ', new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed class TgChat
{
    public long Id { get; init; }
    /// <summary>private | group | supergroup | channel</summary>
    public string Type { get; init; } = "private";
    public string? Title { get; init; }
    public string? Username { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
}

public sealed class TgBusinessConnection
{
    public string Id { get; init; } = "";
    public TgUser User { get; init; } = new();
    public long UserChatId { get; init; }
    public bool IsEnabled { get; init; }
}

public sealed class TgCallbackQuery
{
    public string Id { get; init; } = "";
    public TgUser From { get; init; } = new();
    public string? Data { get; init; }
    public TgMessage? Message { get; init; }
}

internal sealed class TgResponse<T>
{
    public bool Ok { get; init; }
    public T? Result { get; init; }
    public string? Description { get; init; }
    public int? ErrorCode { get; init; }
}

public sealed record InlineButton(string Text, string? Url = null, string? CallbackData = null);

/// <summary>
/// Тонкий клиент Telegram Bot API на HttpClient. Можно заменить на пакет Telegram.Bot —
/// он поддерживает Business-методы, но часто ломает API между мажорными версиями.
/// </summary>
public sealed class TelegramApiClient(HttpClient http, IOptions<TelegramOptions> options)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private string BaseUrl => $"https://api.telegram.org/bot{options.Value.BotToken}/";

    public Task<TgMessage> SendMessageAsync(long chatId, string text, string? businessConnectionId = null,
        IReadOnlyList<IReadOnlyList<InlineButton>>? keyboard = null, long? replyToMessageId = null, CancellationToken ct = default) =>
        CallAsync<TgMessage>("sendMessage", new
        {
            chat_id = chatId,
            text,
            business_connection_id = businessConnectionId,
            reply_parameters = replyToMessageId is null ? null : new { message_id = replyToMessageId, allow_sending_without_reply = true },
            reply_markup = keyboard is null ? null : new
            {
                inline_keyboard = keyboard.Select(row => row.Select(b => new { text = b.Text, url = b.Url, callback_data = b.CallbackData }))
            },
            link_preview_options = new { is_disabled = true }
        }, ct);

    public Task<bool> SendChatActionAsync(long chatId, string action, string? businessConnectionId, CancellationToken ct) =>
        CallAsync<bool>("sendChatAction", new { chat_id = chatId, action, business_connection_id = businessConnectionId }, ct);

    public Task<TgBusinessConnection> GetBusinessConnectionAsync(string id, CancellationToken ct) =>
        CallAsync<TgBusinessConnection>("getBusinessConnection", new { business_connection_id = id }, ct);

    public Task<bool> AnswerCallbackQueryAsync(string id, string? text, CancellationToken ct) =>
        CallAsync<bool>("answerCallbackQuery", new { callback_query_id = id, text }, ct);

    public Task<bool> SetWebhookAsync(string url, string secret, CancellationToken ct) =>
        CallAsync<bool>("setWebhook", new
        {
            url,
            secret_token = secret,
            allowed_updates = new[] { "message", "business_connection", "business_message", "callback_query" },
            drop_pending_updates = false
        }, ct);

    private async Task<T> CallAsync<T>(string method, object payload, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(BaseUrl + method, payload, Json, ct);
        var body = await response.Content.ReadFromJsonAsync<TgResponse<T>>(Json, ct);
        if (body is null || !body.Ok)
            throw new TelegramApiException(method, body?.ErrorCode ?? (int)response.StatusCode, body?.Description ?? "empty response");
        return body.Result!;
    }
}

public sealed class TelegramApiException(string method, int code, string description)
    : Exception($"Telegram {method} failed: {code} {description}")
{
    public int Code { get; } = code;
}
