using LeadBot.Domain.Leads;

namespace LeadBot.Application.Conversation;

/// <summary>Имена стадий/услуг для LLM (snake_case) и человекочитаемые цели стадий.</summary>
public static class StageNames
{
    private static readonly Dictionary<LeadStage, string> ToName = new()
    {
        [LeadStage.New] = "new",
        [LeadStage.Qualifying] = "qualifying",
        [LeadStage.Discovery] = "discovery",
        [LeadStage.Qualified] = "qualified",
        [LeadStage.CallScheduled] = "call_scheduled",
        [LeadStage.ProposalSent] = "proposal_sent",
        [LeadStage.Negotiation] = "negotiation",
        [LeadStage.Won] = "won",
        [LeadStage.Lost] = "lost",
        [LeadStage.Nurturing] = "nurturing",
    };

    private static readonly Dictionary<string, LeadStage> FromName =
        ToName.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string Name(LeadStage stage) => ToName[stage];

    public static bool TryParse(string? name, out LeadStage stage) =>
        FromName.TryGetValue(name?.Trim().ToLowerInvariant() ?? "", out stage);

    public static string Goal(LeadStage stage) => stage switch
    {
        LeadStage.New or LeadStage.Qualifying =>
            "Понять, с чем пришёл клиент: какая задача и какой тип услуги (разработка, внедрение ИИ, консалтинг, поддержка). Узнай, как к нему обращаться.",
        LeadStage.Discovery =>
            "Уточнить бизнес-контекст, ориентир по бюджету, желаемые сроки, кто принимает решение и удобный контакт.",
        LeadStage.Qualified =>
            "Предложить короткий созвон (20–30 мин) для брифа: получи свободные слоты через get_call_slots и запиши через book_call.",
        LeadStage.CallScheduled =>
            "Созвон назначен. Отвечай на вопросы, напомни время, не продавай повторно. Если нужно перенести — book_call с новым слотом.",
        LeadStage.ProposalSent or LeadStage.Negotiation =>
            "С клиентом работает менеджер. Отвечай только на простые вопросы, всё остальное — handoff_to_human.",
        LeadStage.Nurturing =>
            "Клиент сейчас не готов. Без давления: поддержи контакт, полезная информация, предложи вернуться к разговору позже.",
        LeadStage.Won => "Клиент уже работает с нами — передай вопрос менеджеру проекта (handoff_to_human).",
        LeadStage.Lost => "Клиент ранее отказался. Если он вернулся с новым интересом — начни квалификацию заново.",
        _ => ""
    };

    public static ServiceType ParseService(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "custom_development" => ServiceType.CustomDevelopment,
        "ai_integration" => ServiceType.AiIntegration,
        "consulting" => ServiceType.Consulting,
        "support" => ServiceType.Support,
        _ => ServiceType.Unknown
    };

    public static string ServiceName(ServiceType type) => type switch
    {
        ServiceType.CustomDevelopment => "custom_development",
        ServiceType.AiIntegration => "ai_integration",
        ServiceType.Consulting => "consulting",
        ServiceType.Support => "support",
        _ => "unknown"
    };
}
