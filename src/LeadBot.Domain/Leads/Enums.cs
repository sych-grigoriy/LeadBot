namespace LeadBot.Domain.Leads;

/// <summary>Канал, из которого пришёл клиент.</summary>
public enum ChannelKind
{
    Telegram,
    Max
}

/// <summary>Стадии воронки для услуг «разработка ПО / внедрение ИИ».</summary>
public enum LeadStage
{
    /// <summary>Только что написал, ничего не знаем.</summary>
    New = 0,
    /// <summary>Выясняем, что за задача и какой тип услуги.</summary>
    Qualifying = 10,
    /// <summary>Глубже: бюджет, сроки, ЛПР, контакты.</summary>
    Discovery = 20,
    /// <summary>Лид квалифицирован, можно назначать созвон.</summary>
    Qualified = 30,
    /// <summary>Созвон / бриф назначен.</summary>
    CallScheduled = 40,
    /// <summary>КП отправлено (ставит человек или amoCRM).</summary>
    ProposalSent = 50,
    /// <summary>Переговоры (человек или amoCRM).</summary>
    Negotiation = 60,
    Won = 100,
    Lost = 110,
    /// <summary>«Не сейчас» — подогрев, вернёмся позже.</summary>
    Nurturing = 120
}

public enum ServiceType
{
    Unknown = 0,
    /// <summary>Заказная разработка: веб, мобильные, десктоп, бэкенд.</summary>
    CustomDevelopment,
    /// <summary>Внедрение ИИ: боты, ассистенты, RAG, автоматизация процессов.</summary>
    AiIntegration,
    /// <summary>Консалтинг / аудит / архитектура.</summary>
    Consulting,
    /// <summary>Поддержка и доработка существующей системы.</summary>
    Support
}

/// <summary>Кто сейчас ведёт диалог.</summary>
public enum ConversationMode
{
    Bot,
    /// <summary>Человек перехватил диалог — бот молчит до истечения таймаута или ручного возврата.</summary>
    Human
}

/// <summary>Кто инициировал смену стадии.</summary>
public enum TransitionSource
{
    Bot,
    Human,
    Crm,
    System
}

public enum MessageAuthor
{
    Client,
    Bot,
    /// <summary>Владелец/менеджер написал сам (например, из своего Telegram через Business).</summary>
    Manager,
    /// <summary>Служебная запись (смена стадии и т.п.), в LLM не отправляется как реплика.</summary>
    System
}
