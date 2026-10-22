using System.Collections.Concurrent;
using LeadBot.Application;
using LeadBot.Application.Abstractions;
using LeadBot.Domain.Leads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadBot.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

/// <summary>
/// Хранилище в памяти — для разработки и демо. Данные теряются при перезапуске.
/// В проде — LeadBot.Persistence.Postgres.
/// </summary>
public sealed class InMemoryLeadRepository : ILeadRepository
{
    private readonly ConcurrentDictionary<Guid, Lead> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _byKey = new();

    public Task<Lead?> FindByIdentityAsync(ChannelKind channel, string userId, CancellationToken ct) =>
        Task.FromResult(_byKey.TryGetValue($"{channel}:{userId}", out var id) && _byId.TryGetValue(id, out var l) ? l : null);

    public Task<Lead?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_byId.TryGetValue(id, out var l) ? l : null);

    public Task<Lead?> FindByCrmLeadIdAsync(long crmLeadId, CancellationToken ct) =>
        Task.FromResult(_byId.Values.FirstOrDefault(l => l.CrmLeadId == crmLeadId));

    public Task<IReadOnlyList<Lead>> GetDueFollowUpsAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Lead>>(_byId.Values
            .Where(l => l.NextFollowUpAt <= now && l.Mode == ConversationMode.Bot && !l.IsClosed)
            .OrderBy(l => l.NextFollowUpAt)
            .Take(limit)
            .ToList());

    public Task SaveAsync(Lead lead, CancellationToken ct)
    {
        _byId[lead.Id] = lead;
        _byKey[lead.Identity.Key] = lead.Id;
        return Task.CompletedTask;
    }

    public Task SetCrmIdsAsync(Guid leadId, long crmLeadId, long? crmContactId, CancellationToken ct)
    {
        if (_byId.TryGetValue(leadId, out var lead))
        {
            lead.CrmLeadId = crmLeadId;
            lead.CrmContactId = crmContactId;
        }
        return Task.CompletedTask;
    }
}

public sealed class WorkingHoursCalendarOptions
{
    public const string Section = "Calendar";
    public int DayStartHour { get; set; } = 10;
    public int DayEndHour { get; set; } = 18;
    public int SlotMinutes { get; set; } = 30;
    /// <summary>Не раньше чем через N часов от текущего момента.</summary>
    public int MinLeadHours { get; set; } = 3;
    public int DaysAhead { get; set; } = 5;
}

/// <summary>
/// Простой календарь: рабочие часы будни, брони в памяти.
/// TODO: заменить на интеграцию с Google Calendar / Яндекс Календарём (CalDAV) — интерфейс тот же.
/// </summary>
public sealed class WorkingHoursCalendar(
    IClock clock,
    IOptions<FunnelOptions> funnel,
    IOptions<WorkingHoursCalendarOptions> options) : ICallCalendar
{
    private static readonly ConcurrentDictionary<DateTimeOffset, Guid> Booked = new();

    public Task<IReadOnlyList<DateTimeOffset>> GetFreeSlotsAsync(int maxSlots, CancellationToken ct)
    {
        var o = options.Value;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(funnel.Value.TimeZone);
        var earliest = clock.Now.AddHours(o.MinLeadHours);
        var localToday = TimeZoneInfo.ConvertTime(clock.Now, tz).Date;
        var result = new List<DateTimeOffset>();

        for (var d = 0; d <= o.DaysAhead + 2 && result.Count < maxSlots; d++)
        {
            var day = localToday.AddDays(d);
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            for (var t = day.AddHours(o.DayStartHour); t < day.AddHours(o.DayEndHour) && result.Count < maxSlots; t = t.AddMinutes(o.SlotMinutes))
            {
                var slot = new DateTimeOffset(t, tz.GetUtcOffset(t));
                if (slot >= earliest && !Booked.ContainsKey(slot))
                    result.Add(slot);
            }
        }

        // Не вываливаем все слоты одного дня — даём выбор из разных дней.
        var spread = result.GroupBy(s => s.Date).SelectMany(g => g.Take(2)).Take(maxSlots).ToList();
        return Task.FromResult<IReadOnlyList<DateTimeOffset>>(spread);
    }

    public Task<bool> TryBookAsync(DateTimeOffset slot, Lead lead, CancellationToken ct) =>
        Task.FromResult(Booked.TryAdd(slot, lead.Id) || Booked[slot] == lead.Id);
}

/// <summary>Заглушка уведомлений — пишет в лог. Telegram-реализация подменяет её, если задан OwnerChatId.</summary>
public sealed class LogOwnerNotifier(ILogger<LogOwnerNotifier> log) : IOwnerNotifier
{
    public Task NotifyAsync(Lead lead, string text, bool urgent, CancellationToken ct)
    {
        log.LogWarning("[OWNER{Urgent}] Лид {LeadId} ({Name}): {Text}", urgent ? " СРОЧНО" : "", lead.Id, lead.Profile.Name, text);
        return Task.CompletedTask;
    }
}

public static class InfrastructureExtensions
{
    public static IServiceCollection AddLeadBotInfrastructure(this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration config)
    {
        services.Configure<WorkingHoursCalendarOptions>(config.GetSection(WorkingHoursCalendarOptions.Section));
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ILeadRepository, InMemoryLeadRepository>();
        services.TryAddSingleton<ICallCalendar, WorkingHoursCalendar>();
        services.TryAddScoped<IOwnerNotifier, LogOwnerNotifier>();
        return services;
    }
}
