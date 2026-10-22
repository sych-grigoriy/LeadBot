namespace LeadBot.Domain.Leads;

/// <summary>
/// Данные, которые бот собирает по ходу диалога. Все поля опциональны —
/// какие обязательны для перехода на стадию, решает <see cref="Funnel.FunnelStateMachine"/>.
/// </summary>
public sealed class LeadProfile
{
    public string? Name { get; set; }
    public string? Company { get; set; }
    /// <summary>Должность / роль клиента.</summary>
    public string? Role { get; set; }
    public bool? IsDecisionMaker { get; set; }

    public ServiceType ServiceType { get; set; } = ServiceType.Unknown;
    /// <summary>Суть задачи своими словами (1–3 предложения).</summary>
    public string? ProblemSummary { get; set; }
    /// <summary>Вилка бюджета как назвал клиент: «до 300 тыс.», «500k–1М».</summary>
    public string? BudgetRange { get; set; }
    /// <summary>Желаемые сроки: «к марту», «ASAP», «в течение квартала».</summary>
    public string? Timeline { get; set; }

    public string? Phone { get; set; }
    public string? Email { get; set; }

    public DateTimeOffset? CallSlot { get; set; }
    public string? LostReason { get; set; }

    /// <summary>Откуда пришёл: личка, группа X, deep-link payload и т.п.</summary>
    public string? Source { get; set; }

    public bool HasContact => !string.IsNullOrWhiteSpace(Phone) || !string.IsNullOrWhiteSpace(Email);
}
