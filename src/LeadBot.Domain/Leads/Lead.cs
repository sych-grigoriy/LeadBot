using LeadBot.Domain.Funnel;

namespace LeadBot.Domain.Leads;

public sealed record ChatMessage(
    MessageAuthor Author,
    string Text,
    DateTimeOffset At,
    /// <summary>ID сообщения в мессенджере — для защиты от повторной доставки вебхука.</summary>
    string? ExternalId = null);

/// <summary>
/// Агрегат «лид»: кто клиент, на какой он стадии, кто ведёт диалог и что уже обсудили.
/// Менять стадию можно только через <see cref="MoveTo"/>, который проверяет правила воронки.
/// </summary>
public sealed class Lead
{
    private readonly List<ChatMessage> _history = [];

    public Guid Id { get; private set; } = Guid.NewGuid();
    public ChannelIdentity Identity { get; private set; }
    public LeadStage Stage { get; private set; } = LeadStage.New;
    public LeadProfile Profile { get; private set; } = new();

    public ConversationMode Mode { get; private set; } = ConversationMode.Bot;
    public DateTimeOffset? HumanTakeoverUntil { get; private set; }

    public long? CrmLeadId { get; set; }
    public long? CrmContactId { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? LastClientMessageAt { get; private set; }

    public DateTimeOffset? NextFollowUpAt { get; private set; }
    public int FollowUpCount { get; private set; }

    public IReadOnlyList<ChatMessage> History => _history;

    public bool IsClosed => Stage is LeadStage.Won or LeadStage.Lost;

    public Lead(ChannelIdentity identity, DateTimeOffset now)
    {
        Identity = identity;
        CreatedAt = UpdatedAt = now;
    }

    // Для ORM / восстановления из хранилища.
    private Lead() => Identity = null!;

    /// <summary>Обновить канальные данные (например, сменился business_connection_id или username).</summary>
    public void RefreshIdentity(ChannelIdentity identity)
    {
        if (identity.Key != Identity.Key)
            throw new InvalidOperationException("Нельзя сменить владельца лида.");
        Identity = identity;
    }

    public bool HasMessage(string externalId) =>
        _history.Exists(m => m.ExternalId == externalId);

    public void AddClientMessage(string text, DateTimeOffset at, string? externalId)
    {
        _history.Add(new ChatMessage(MessageAuthor.Client, text, at, externalId));
        LastClientMessageAt = at;
        // Клиент ответил — счётчик напоминаний сбрасываем.
        FollowUpCount = 0;
        NextFollowUpAt = null;
        Touch(at);
    }

    public void AddBotMessage(string text, DateTimeOffset at) =>
        Append(new ChatMessage(MessageAuthor.Bot, text, at));

    public void AddManagerMessage(string text, DateTimeOffset at, string? externalId) =>
        Append(new ChatMessage(MessageAuthor.Manager, text, at, externalId));

    public void AddSystemNote(string text, DateTimeOffset at) =>
        Append(new ChatMessage(MessageAuthor.System, text, at));

    /// <summary>Сменить стадию через машину состояний. Возвращает результат с текстом ошибки для LLM.</summary>
    public TransitionResult MoveTo(LeadStage target, TransitionSource source, DateTimeOffset now, string? reason = null)
    {
        var result = FunnelStateMachine.Check(this, target, source);
        if (!result.Success || result.From == target)
            return result;

        Stage = target;
        AddSystemNote($"Стадия: {result.From} → {target} ({source}){(reason is null ? "" : $": {reason}")}", now);

        if (IsClosed)
            NextFollowUpAt = null;

        return result;
    }

    public void TakeoverByHuman(DateTimeOffset until, DateTimeOffset now)
    {
        Mode = ConversationMode.Human;
        HumanTakeoverUntil = until;
        NextFollowUpAt = null;
        Touch(now);
    }

    public void ReturnToBot(DateTimeOffset now)
    {
        Mode = ConversationMode.Bot;
        HumanTakeoverUntil = null;
        Touch(now);
    }

    /// <summary>Если таймаут перехвата истёк — вернуть диалог боту.</summary>
    public void ExpireTakeoverIfNeeded(DateTimeOffset now)
    {
        if (Mode == ConversationMode.Human && HumanTakeoverUntil is { } until && until <= now)
            ReturnToBot(now);
    }

    public void ScheduleFollowUp(DateTimeOffset at) => NextFollowUpAt = at;

    public void RegisterFollowUpSent(DateTimeOffset now)
    {
        FollowUpCount++;
        NextFollowUpAt = null;
        Touch(now);
    }

    private void Append(ChatMessage message)
    {
        _history.Add(message);
        Touch(message.At);
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = now;
}
