using LeadBot.Domain.Leads;

namespace LeadBot.Application.Abstractions;

/// <summary>Хранилище лидов. Реализации: InMemory (dev) и EF Core/Postgres (prod).</summary>
public interface ILeadRepository
{
    Task<Lead?> FindByIdentityAsync(ChannelKind channel, string userId, CancellationToken ct);
    Task<Lead?> FindByIdAsync(Guid id, CancellationToken ct);
    Task<Lead?> FindByCrmLeadIdAsync(long crmLeadId, CancellationToken ct);
    Task<IReadOnlyList<Lead>> GetDueFollowUpsAsync(DateTimeOffset now, int limit, CancellationToken ct);
    Task SaveAsync(Lead lead, CancellationToken ct);
    /// <summary>Записать только ID из CRM (вызывается фоновой синхронизацией).</summary>
    Task SetCrmIdsAsync(Guid leadId, long crmLeadId, long? crmContactId, CancellationToken ct);
}

/// <summary>Исходящие сообщения в конкретный мессенджер. Одна реализация на канал.</summary>
public interface IChannelSender
{
    ChannelKind Channel { get; }
    Task SendTextAsync(ChannelIdentity to, string text, CancellationToken ct);
    /// <summary>Показать «печатает…» (не обязательно поддерживается каналом).</summary>
    Task SendTypingAsync(ChannelIdentity to, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Уведомления владельцу/менеджеру: горячий лид, нужен человек, ошибка.</summary>
public interface IOwnerNotifier
{
    Task NotifyAsync(Lead lead, string text, bool urgent, CancellationToken ct);
}

/// <summary>Слоты для созвона. Каркасная реализация — рабочие часы; дальше — Google/Яндекс Календарь.</summary>
public interface ICallCalendar
{
    Task<IReadOnlyList<DateTimeOffset>> GetFreeSlotsAsync(int maxSlots, CancellationToken ct);
    Task<bool> TryBookAsync(DateTimeOffset slot, Lead lead, CancellationToken ct);
}

public interface IClock
{
    DateTimeOffset Now { get; }
}
