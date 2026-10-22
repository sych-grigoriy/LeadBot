using System.Text;
using System.Text.Json;
using LeadBot.Application.Abstractions;
using LeadBot.Domain.Funnel;
using LeadBot.Domain.Leads;
using Microsoft.Extensions.Options;

namespace LeadBot.Application.Conversation;

/// <summary>
/// Собирает системный промпт (роль + состояние лида + правила + описание компании)
/// и историю диалога в формате LLM.
/// </summary>
public sealed class PromptBuilder
{
    private readonly FunnelOptions _options;
    private readonly Lazy<string> _companyProfile;
    private readonly TimeZoneInfo _tz;

    public PromptBuilder(IOptions<FunnelOptions> options)
    {
        _options = options.Value;
        _tz = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZone);
        _companyProfile = new Lazy<string>(() =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, _options.CompanyProfilePath);
            return File.Exists(path) ? File.ReadAllText(path) : "(описание компании не задано — не придумывай факты о компании)";
        });
    }

    public string BuildSystemPrompt(Lead lead, DateTimeOffset now, string? extraInstruction = null)
    {
        var next = FunnelStateMachine.NextOnHappyPath(lead.Stage);
        var missing = next is { } n ? FunnelStateMachine.MissingFor(n, lead.Profile) : [];
        var allowed = FunnelStateMachine.AllowedFrom(lead.Stage, TransitionSource.Bot).Select(StageNames.Name);
        var local = TimeZoneInfo.ConvertTime(now, _tz);

        var channelNote = lead.Identity switch
        {
            { Channel: ChannelKind.Telegram, BusinessConnectionId: not null } =>
                "Ты отвечаешь в личном Telegram-аккаунте владельца компании (Telegram Business). Пиши от лица команды компании.",
            { Channel: ChannelKind.Telegram } => "Ты — бот компании в Telegram.",
            { Channel: ChannelKind.Max } => "Ты — бот компании в мессенджере MAX.",
            _ => ""
        };

        var sb = new StringBuilder();
        sb.AppendLine($"""
            Ты — ИИ-ассистент по работе с клиентами компании, которая занимается разработкой ПО и внедрением ИИ.
            {channelNote}
            Твоя задача — дружелюбно и по делу помочь клиенту, понять его задачу и довести до созвона с командой.

            ## Текущее состояние лида
            Стадия: {StageNames.Name(lead.Stage)}
            Цель на этой стадии: {StageNames.Goal(lead.Stage)}
            Собранные данные: {ProfileJson(lead.Profile)}
            Следующая стадия по воронке: {(next is null ? "—" : StageNames.Name(next.Value))}
            Не хватает для перехода: {(missing.Count == 0 ? "ничего — можно переходить" : string.Join(", ", missing))}
            Стадии, которые ты можешь выставить сейчас: {string.Join(", ", allowed)}
            Текущее время: {local:dd.MM.yyyy HH:mm} ({_options.TimeZone})

            ## Правила общения
            - Пиши коротко: 1–4 предложения, обычный текст без заголовков и таблиц. Язык — как у клиента.
            - Не больше 1–2 вопросов за сообщение. Это разговор, а не анкета.
            - Всё, что клиент сообщил о себе и задаче, сразу сохраняй через update_lead_profile.
            - Как только данных хватает — переводи стадию через move_to_stage (созвон — только через book_call).
            - Цены и сроки называй только как ориентиры из описания компании; точную оценку даёт команда после брифа.
            - Не обещай скидок и не придумывай кейсы, технологии или факты, которых нет в описании компании.
            - Передай диалог человеку (handoff_to_human), если: клиент просит человека; недоволен; вопрос про договор,
              юридические или финансовые условия; задача явно крупная/нестандартная; ты не уверен в ответе.
            - Если клиенту «не сейчас» — переведи в nurturing и запланируй напоминание (schedule_followup).
            - Если клиент прямо спросит, бот ли ты, — честно ответь, что ты ИИ-ассистент, и предложи позвать человека.
            - Реплики с пометкой [Менеджер] написал живой сотрудник — учитывай их и не противоречь им.
            """);

        if (!string.IsNullOrWhiteSpace(extraInstruction))
        {
            sb.AppendLine();
            sb.AppendLine("## Особая инструкция на этот ход");
            sb.AppendLine(extraInstruction);
        }

        sb.AppendLine();
        sb.AppendLine("## О компании");
        sb.AppendLine(_companyProfile.Value);
        return sb.ToString();
    }

    /// <summary>
    /// История в формате LLM: роли должны чередоваться и начинаться с user.
    /// Подряд идущие реплики одной роли склеиваются; системные заметки пропускаются.
    /// </summary>
    public List<LlmMessage> BuildHistory(Lead lead, string? syntheticUserTurn = null)
    {
        var result = new List<LlmMessage>();
        var window = lead.History
            .Where(m => m.Author != MessageAuthor.System)
            .TakeLast(_options.HistoryWindow);

        LlmRole? role = null;
        var buffer = new StringBuilder();

        void Flush()
        {
            if (role is null || buffer.Length == 0) return;
            result.Add(new LlmMessage(role.Value, [new LlmText(buffer.ToString().Trim())]));
            buffer.Clear();
        }

        foreach (var m in window)
        {
            var (r, text) = m.Author switch
            {
                MessageAuthor.Client => (LlmRole.User, m.Text),
                MessageAuthor.Manager => (LlmRole.Assistant, $"[Менеджер] {m.Text}"),
                _ => (LlmRole.Assistant, m.Text)
            };
            if (role != r) { Flush(); role = r; }
            buffer.AppendLine(text);
        }
        Flush();

        // API требует первую реплику от user.
        while (result.Count > 0 && result[0].Role != LlmRole.User)
            result.RemoveAt(0);

        if (syntheticUserTurn is not null || result.Count == 0 || result[^1].Role != LlmRole.User)
            result.Add(LlmMessage.UserText(syntheticUserTurn ?? "(клиент ждёт ответа)"));

        return result;
    }

    private static string ProfileJson(LeadProfile p) => JsonSerializer.Serialize(new
    {
        name = p.Name,
        company = p.Company,
        role = p.Role,
        is_decision_maker = p.IsDecisionMaker,
        service_type = StageNames.ServiceName(p.ServiceType),
        problem_summary = p.ProblemSummary,
        budget_range = p.BudgetRange,
        timeline = p.Timeline,
        phone = p.Phone,
        email = p.Email,
        call_slot = p.CallSlot,
        source = p.Source
    }, new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    });
}
