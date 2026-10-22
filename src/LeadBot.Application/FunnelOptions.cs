namespace LeadBot.Application;

public sealed class FunnelOptions
{
    public const string Section = "Funnel";

    /// <summary>Сколько ждать после сообщения клиента, прежде чем отвечать (клиенты пишут очередями).</summary>
    public int DebounceSeconds { get; set; } = 4;

    /// <summary>На сколько бот замолкает, если менеджер сам ответил в чате.</summary>
    public int HumanTakeoverHours { get; set; } = 12;

    /// <summary>Через сколько часов молчания клиента отправить напоминание.</summary>
    public int FollowUpAfterHours { get; set; } = 24;

    /// <summary>Максимум напоминаний подряд; после — перевод в Nurturing.</summary>
    public int MaxFollowUps { get; set; } = 2;

    /// <summary>Сколько последних реплик отдавать в LLM.</summary>
    public int HistoryWindow { get; set; } = 30;

    /// <summary>Максимум шагов «модель → инструмент → модель» на один ответ.</summary>
    public int MaxAgentSteps { get; set; } = 6;

    /// <summary>Часовой пояс бизнеса (для слотов созвона и «тихих часов»).</summary>
    public string TimeZone { get; set; } = "Asia/Vladivostok";

    /// <summary>Тихие часы — напоминания не отправляются (локальное время бизнеса).</summary>
    public int QuietHoursFrom { get; set; } = 21;
    public int QuietHoursTo { get; set; } = 9;

    /// <summary>Путь к описанию компании/услуг/цен для системного промпта.</summary>
    public string CompanyProfilePath { get; set; } = "prompts/company.md";

    /// <summary>Ответ, если LLM недоступна.</summary>
    public string FallbackReply { get; set; } =
        "Спасибо за сообщение! Передал его коллеге — ответим в ближайшее время.";
}
