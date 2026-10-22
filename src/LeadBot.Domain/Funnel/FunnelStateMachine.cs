using LeadBot.Domain.Leads;

namespace LeadBot.Domain.Funnel;

public sealed record TransitionResult(bool Success, LeadStage From, LeadStage To, string? Error = null)
{
    public static TransitionResult Ok(LeadStage from, LeadStage to) => new(true, from, to);
    public static TransitionResult Fail(LeadStage from, LeadStage to, string error) => new(false, from, to, error);
}

/// <summary>
/// Машина состояний воронки. Чистая логика без I/O — легко покрыть тестами.
///
/// Правила:
///  1. Разрешены только переходы из таблицы <see cref="Transitions"/>.
///  2. Для входа в стадию должны быть заполнены обязательные поля (<see cref="MissingFor"/>).
///  3. Бот не может сам ставить «поздние» стадии (КП, переговоры, сделка) — это делает человек или amoCRM.
///  4. amoCRM — источник истины для поздних стадий: переход с источником Crm применяется без проверок.
/// </summary>
public static class FunnelStateMachine
{
    private static readonly Dictionary<LeadStage, LeadStage[]> Transitions = new()
    {
        [LeadStage.New] = [LeadStage.Qualifying, LeadStage.Nurturing, LeadStage.Lost],
        [LeadStage.Qualifying] = [LeadStage.Discovery, LeadStage.Nurturing, LeadStage.Lost],
        [LeadStage.Discovery] = [LeadStage.Qualified, LeadStage.Qualifying, LeadStage.Nurturing, LeadStage.Lost],
        [LeadStage.Qualified] = [LeadStage.CallScheduled, LeadStage.Nurturing, LeadStage.Lost],
        [LeadStage.CallScheduled] = [LeadStage.ProposalSent, LeadStage.Qualified, LeadStage.Nurturing, LeadStage.Lost],
        [LeadStage.ProposalSent] = [LeadStage.Negotiation, LeadStage.Won, LeadStage.Nurturing, LeadStage.Lost],
        [LeadStage.Negotiation] = [LeadStage.Won, LeadStage.Nurturing, LeadStage.Lost],
        [LeadStage.Nurturing] = [LeadStage.Qualifying, LeadStage.Discovery, LeadStage.Lost],
        [LeadStage.Lost] = [LeadStage.Qualifying], // клиент вернулся
        [LeadStage.Won] = [],
    };

    /// <summary>Стадии, которые бот может выставить сам.</summary>
    private static readonly HashSet<LeadStage> BotAllowedTargets =
    [
        LeadStage.Qualifying, LeadStage.Discovery, LeadStage.Qualified,
        LeadStage.CallScheduled, LeadStage.Nurturing, LeadStage.Lost
    ];

    /// <summary>«Счастливый путь» — куда бот ведёт клиента дальше.</summary>
    public static LeadStage? NextOnHappyPath(LeadStage stage) => stage switch
    {
        LeadStage.New => LeadStage.Qualifying,
        LeadStage.Qualifying => LeadStage.Discovery,
        LeadStage.Discovery => LeadStage.Qualified,
        LeadStage.Qualified => LeadStage.CallScheduled,
        LeadStage.Nurturing => LeadStage.Qualifying,
        _ => null
    };

    public static IReadOnlyList<LeadStage> AllowedFrom(LeadStage stage, TransitionSource source) =>
        Transitions[stage]
            .Where(t => source != TransitionSource.Bot || BotAllowedTargets.Contains(t))
            .ToList();

    /// <summary>Каких данных не хватает, чтобы войти в стадию. Пустой список — можно переходить.</summary>
    public static IReadOnlyList<string> MissingFor(LeadStage target, LeadProfile p)
    {
        var missing = new List<string>();
        switch (target)
        {
            case LeadStage.Discovery:
                if (p.ServiceType == ServiceType.Unknown) missing.Add("service_type");
                if (string.IsNullOrWhiteSpace(p.ProblemSummary)) missing.Add("problem_summary");
                break;
            case LeadStage.Qualified:
                // Всё, что нужно для Discovery, плюс бюджет, сроки и имя.
                missing.AddRange(MissingFor(LeadStage.Discovery, p));
                if (string.IsNullOrWhiteSpace(p.BudgetRange)) missing.Add("budget_range");
                if (string.IsNullOrWhiteSpace(p.Timeline)) missing.Add("timeline");
                if (string.IsNullOrWhiteSpace(p.Name)) missing.Add("name");
                break;
            case LeadStage.CallScheduled:
                missing.AddRange(MissingFor(LeadStage.Qualified, p));
                if (p.CallSlot is null) missing.Add("call_slot");
                break;
            case LeadStage.Lost:
                if (string.IsNullOrWhiteSpace(p.LostReason)) missing.Add("lost_reason");
                break;
        }
        return missing;
    }

    /// <summary>Проверить переход, не меняя лид.</summary>
    public static TransitionResult Check(Lead lead, LeadStage target, TransitionSource source)
    {
        var from = lead.Stage;

        if (from == target)
            return TransitionResult.Ok(from, target); // идемпотентно: важно для эха вебхуков amoCRM

        if (source == TransitionSource.Crm)
            return TransitionResult.Ok(from, target);

        if (!Transitions[from].Contains(target))
            return TransitionResult.Fail(from, target,
                $"Переход {from} → {target} не разрешён. Допустимо: {string.Join(", ", Transitions[from])}.");

        if (source == TransitionSource.Bot && !BotAllowedTargets.Contains(target))
            return TransitionResult.Fail(from, target,
                $"Стадию {target} выставляет только менеджер. Если клиент готов — передай диалог человеку (handoff_to_human).");

        var missing = MissingFor(target, lead.Profile);
        if (missing.Count > 0)
            return TransitionResult.Fail(from, target,
                $"Для стадии {target} не хватает данных: {string.Join(", ", missing)}. Сначала узнай их у клиента и сохрани через update_lead_profile.");

        return TransitionResult.Ok(from, target);
    }
}
