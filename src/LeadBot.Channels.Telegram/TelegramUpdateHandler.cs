using System.Collections.Concurrent;
using LeadBot.Application.Conversation;
using LeadBot.Application.Messaging;
using LeadBot.Domain.Leads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadBot.Channels.Telegram;

/// <summary>Подключения Telegram Business: id подключения → владелец аккаунта.</summary>
public sealed class BusinessConnectionStore
{
    public sealed record Entry(string Id, long OwnerUserId, bool IsEnabled);
    private readonly ConcurrentDictionary<string, Entry> _map = new();

    public Entry? Get(string id) => _map.TryGetValue(id, out var e) ? e : null;
    public void Upsert(Entry entry) => _map[entry.Id] = entry;
}

/// <summary>Запоминает вопрос, заданный в группе, чтобы бот подхватил его, когда клиент перейдёт в личку.</summary>
public sealed class GroupQuestionCache
{
    public sealed record Entry(string ChatTitle, string Text, DateTimeOffset At);
    private readonly ConcurrentDictionary<long, Entry> _map = new();

    public void Put(long userId, Entry e) => _map[userId] = e;
    public Entry? Take(long userId) =>
        _map.TryRemove(userId, out var e) && e.At > DateTimeOffset.UtcNow.AddDays(-2) ? e : null;
}

/// <summary>
/// Разбор апдейтов Telegram:
///  • business_message — клиент написал вам в личный аккаунт (или вы ответили сами → бот уступает);
///  • message в личке бота — клиент пришёл по ссылке /start;
///  • message в группе — короткий ответ и кнопка «перейти в личку»;
///  • callback_query — кнопки владельца «вернуть боту / забрать себе».
/// </summary>
public sealed class TelegramUpdateHandler(
    IncomingMessageQueue queue,
    BusinessConnectionStore connections,
    GroupQuestionCache groupQuestions,
    TelegramApiClient api,
    ConversationOrchestrator orchestrator,
    IOptions<TelegramOptions> options,
    ILogger<TelegramUpdateHandler> log)
{
    private TelegramOptions Opt => options.Value;

    public async Task HandleAsync(TgUpdate update, CancellationToken ct)
    {
        if (update.BusinessConnection is { } bc)
        {
            connections.Upsert(new(bc.Id, bc.User.Id, bc.IsEnabled));
            log.LogInformation("Business connection {Id}: enabled={Enabled}", bc.Id, bc.IsEnabled);
        }

        if (update.BusinessMessage is { } bm)
            await HandleBusinessMessageAsync(bm, ct);
        else if (update.Message is { } m)
        {
            if (m.Chat.Type == "private") await HandleDirectAsync(m, ct);
            else if (m.Chat.Type is "group" or "supergroup") await HandleGroupAsync(m, ct);
        }
        else if (update.CallbackQuery is { } cq)
            await HandleCallbackAsync(cq, ct);
    }

    private async Task HandleBusinessMessageAsync(TgMessage m, CancellationToken ct)
    {
        if (m.SenderBusinessBot is not null || m.BusinessConnectionId is null)
            return; // эхо наших собственных ответов

        var conn = connections.Get(m.BusinessConnectionId);
        if (conn is null)
        {
            var fetched = await api.GetBusinessConnectionAsync(m.BusinessConnectionId, ct);
            conn = new(fetched.Id, fetched.User.Id, fetched.IsEnabled);
            connections.Upsert(conn);
        }
        if (!conn.IsEnabled) return;

        var text = ExtractText(m);
        if (text is null) return;

        // В бизнес-чате chat — это собеседник (клиент), from — кто написал (клиент или владелец).
        var identity = new ChannelIdentity(
            ChannelKind.Telegram,
            UserId: m.Chat.Id.ToString(),
            ChatId: m.Chat.Id.ToString(),
            DisplayName: Join(m.Chat.FirstName, m.Chat.LastName),
            Username: m.Chat.Username,
            BusinessConnectionId: m.BusinessConnectionId);

        var kind = m.From?.Id == conn.OwnerUserId ? IncomingKind.ManagerReply : IncomingKind.ClientText;
        await queue.EnqueueAsync(new IncomingMessage(identity, kind, text, $"tgb:{m.Chat.Id}:{m.MessageId}",
            DateTimeOffset.FromUnixTimeSeconds(m.Date)), ct);
    }

    private async Task HandleDirectAsync(TgMessage m, CancellationToken ct)
    {
        if (m.From is null || m.From.IsBot) return;
        if (Opt.OwnerChatId == m.From.Id) return; // чат владельца с ботом — только для уведомлений

        var text = ExtractText(m);
        if (text is null) return;

        var identity = new ChannelIdentity(ChannelKind.Telegram, m.From.Id.ToString(), m.Chat.Id.ToString(),
            m.From.DisplayName, m.From.Username);
        var at = DateTimeOffset.FromUnixTimeSeconds(m.Date);
        var externalId = $"tg:{m.Chat.Id}:{m.MessageId}";

        if (text.StartsWith("/start", StringComparison.Ordinal))
        {
            var payload = text.Length > 6 ? text[6..].Trim() : null;
            if (groupQuestions.Take(m.From.Id) is { } q)
                payload = $"группа «{q.ChatTitle}», вопрос: {q.Text}";

            await queue.EnqueueAsync(new IncomingMessage(identity, IncomingKind.Start, text, externalId, at, payload), ct);
            return;
        }

        await queue.EnqueueAsync(new IncomingMessage(identity, IncomingKind.ClientText, text, externalId, at), ct);
    }

    /// <summary>В группе воронку не ведём: коротко отвечаем и зовём в личку (там бот получит контекст вопроса).</summary>
    private async Task HandleGroupAsync(TgMessage m, CancellationToken ct)
    {
        if (m.From is null || m.From.IsBot || string.IsNullOrWhiteSpace(Opt.BotUsername)) return;
        var text = m.Text ?? m.Caption;
        if (string.IsNullOrWhiteSpace(text)) return;

        var mentioned = text.Contains("@" + Opt.BotUsername, StringComparison.OrdinalIgnoreCase);
        var replyToBot = m.ReplyToMessage?.From?.Username?.Equals(Opt.BotUsername, StringComparison.OrdinalIgnoreCase) == true;
        var keyword = Opt.GroupKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (!mentioned && !replyToBot && !keyword) return;

        groupQuestions.Put(m.From.Id, new(m.Chat.Title ?? "группа", text, DateTimeOffset.UtcNow));

        var link = $"https://t.me/{Opt.BotUsername}?start=grp_{Math.Abs(m.Chat.Id)}";
        await api.SendMessageAsync(m.Chat.Id, Opt.GroupReplyText,
            keyboard: [[new InlineButton("Написать в личку", Url: link)]],
            replyToMessageId: m.MessageId, ct: ct);
    }

    private async Task HandleCallbackAsync(TgCallbackQuery cq, CancellationToken ct)
    {
        if (Opt.OwnerChatId != cq.From.Id || cq.Data is null) return;

        // Формат: lead:bot:<guid> | lead:human:<guid>
        var parts = cq.Data.Split(':');
        if (parts.Length == 3 && parts[0] == "lead" && Guid.TryParse(parts[2], out var leadId))
        {
            var mode = parts[1] == "bot" ? ConversationMode.Bot : ConversationMode.Human;
            await orchestrator.SetModeAsync(leadId, mode, ct);
            await api.AnswerCallbackQueryAsync(cq.Id, mode == ConversationMode.Bot ? "Диалог снова ведёт бот" : "Бот замолчал, диалог ваш", ct);
        }
    }

    /// <summary>Текст или описание вложения — чтобы бот понимал, что клиент что-то прислал.</summary>
    private static string? ExtractText(TgMessage m)
    {
        if (!string.IsNullOrWhiteSpace(m.Text)) return m.Text;
        var attachment = m switch
        {
            { Photo: not null } => "фото",
            { Document: not null } => "файл",
            { Voice: not null } => "голосовое сообщение",
            { VideoNote: not null } => "видеосообщение",
            { Sticker: not null } => "стикер",
            _ => null
        };
        if (attachment is null) return null;
        return string.IsNullOrWhiteSpace(m.Caption)
            ? $"[клиент прислал {attachment}]"
            : $"[клиент прислал {attachment}] {m.Caption}";
    }

    private static string? Join(string? a, string? b)
    {
        var s = string.Join(' ', new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return s.Length == 0 ? null : s;
    }
}
