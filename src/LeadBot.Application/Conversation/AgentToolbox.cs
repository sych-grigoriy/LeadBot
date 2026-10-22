using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeadBot.Application.Abstractions;
using LeadBot.Application.Crm;
using LeadBot.Domain.Leads;
using Microsoft.Extensions.Options;

namespace LeadBot.Application.Conversation;

/// <summary>Состояние одного «хода» агента: что изменилось, чтобы потом синхронизировать CRM и т.п.</summary>
public sealed class AgentTurnContext(Lead lead, DateTimeOffset now)
{
    public Lead Lead { get; } = lead;
    public DateTimeOffset Now { get; } = now;
    public bool ProfileChanged { get; set; }
    public bool StageChanged { get; set; }
    public bool HandedOff { get; set; }
    public bool FollowUpScheduled { get; set; }
}

public sealed record ToolOutcome(string Content, bool IsError = false);

/// <summary>
/// Инструменты, которые LLM может вызывать. Модель только «предлагает» действия —
/// все проверки (правила воронки, валидность слотов) выполняются здесь, в коде.
/// </summary>
public sealed class AgentToolbox(
    ICallCalendar calendar,
    IOwnerNotifier notifier,
    CrmSyncQueue crm,
    IOptions<FunnelOptions> options)
{
    public IReadOnlyList<LlmTool> Definitions { get; } =
    [
        new("update_lead_profile",
            "Сохранить данные о клиенте и задаче, как только клиент их сообщил. Передавай только известные поля.",
            Schema("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "Как обращаться к клиенту" },
                "company": { "type": "string" },
                "role": { "type": "string", "description": "Должность/роль" },
                "is_decision_maker": { "type": "boolean", "description": "Принимает ли решение сам" },
                "service_type": { "type": "string", "enum": ["custom_development", "ai_integration", "consulting", "support"] },
                "problem_summary": { "type": "string", "description": "Суть задачи в 1–3 предложениях" },
                "budget_range": { "type": "string", "description": "Ориентир бюджета словами клиента" },
                "timeline": { "type": "string", "description": "Желаемые сроки словами клиента" },
                "phone": { "type": "string" },
                "email": { "type": "string" }
              }
            }
            """)),
        new("move_to_stage",
            "Перевести лид на другую стадию воронки. Если данных не хватает — вернётся ошибка со списком недостающих полей.",
            Schema("""
            {
              "type": "object",
              "properties": {
                "stage": { "type": "string", "enum": ["qualifying", "discovery", "qualified", "nurturing", "lost"] },
                "reason": { "type": "string" },
                "lost_reason": { "type": "string", "description": "Обязательно для stage=lost" }
              },
              "required": ["stage", "reason"]
            }
            """)),
        new("get_call_slots",
            "Получить ближайшие свободные слоты для созвона-брифа (время в часовом поясе компании).",
            Schema("""{ "type": "object", "properties": {} }""")),
        new("book_call",
            "Записать клиента на созвон в один из слотов, полученных через get_call_slots. Переводит лид в call_scheduled.",
            Schema("""
            {
              "type": "object",
              "properties": { "slot": { "type": "string", "description": "Слот в формате ISO 8601 ровно как из get_call_slots" } },
              "required": ["slot"]
            }
            """)),
        new("handoff_to_human",
            "Передать диалог живому сотруднику. После этого бот замолкает в этом чате. Клиенту скажи, что подключится коллега.",
            Schema("""
            {
              "type": "object",
              "properties": {
                "reason": { "type": "string" },
                "urgent": { "type": "boolean", "description": "true — горячий лид или недовольный клиент" }
              },
              "required": ["reason"]
            }
            """)),
        new("schedule_followup",
            "Запланировать напоминание клиенту, если он попросил вернуться позже.",
            Schema("""
            {
              "type": "object",
              "properties": {
                "in_hours": { "type": "integer", "minimum": 1, "maximum": 2160 },
                "note": { "type": "string", "description": "О чём напомнить" }
              },
              "required": ["in_hours"]
            }
            """)),
    ];

    public async Task<ToolOutcome> ExecuteAsync(AgentTurnContext ctx, LlmToolUse call, CancellationToken ct)
    {
        try
        {
            return call.Name switch
            {
                "update_lead_profile" => UpdateProfile(ctx, call.Input),
                "move_to_stage" => MoveToStage(ctx, call.Input),
                "get_call_slots" => await GetSlotsAsync(ct),
                "book_call" => await BookCallAsync(ctx, call.Input, ct),
                "handoff_to_human" => await HandoffAsync(ctx, call.Input, ct),
                "schedule_followup" => ScheduleFollowUp(ctx, call.Input),
                _ => new ToolOutcome($"Неизвестный инструмент {call.Name}", true)
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return new ToolOutcome($"Некорректные аргументы: {ex.Message}", true);
        }
    }

    private static ToolOutcome UpdateProfile(AgentTurnContext ctx, JsonElement input)
    {
        var p = ctx.Lead.Profile;
        var updated = new List<string>();

        void Set(string key, Action<string> apply)
        {
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(v.GetString()))
            {
                apply(v.GetString()!.Trim());
                updated.Add(key);
            }
        }

        Set("name", v => p.Name = v);
        Set("company", v => p.Company = v);
        Set("role", v => p.Role = v);
        Set("problem_summary", v => p.ProblemSummary = v);
        Set("budget_range", v => p.BudgetRange = v);
        Set("timeline", v => p.Timeline = v);
        Set("phone", v => p.Phone = v);
        Set("email", v => p.Email = v);
        Set("service_type", v => p.ServiceType = StageNames.ParseService(v));

        if (input.TryGetProperty("is_decision_maker", out var dm) && dm.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            p.IsDecisionMaker = dm.GetBoolean();
            updated.Add("is_decision_maker");
        }

        if (updated.Count == 0)
            return new ToolOutcome("Ничего не обновлено: не передано ни одного поля.", true);

        ctx.ProfileChanged = true;
        return new ToolOutcome($"Сохранено: {string.Join(", ", updated)}.");
    }

    private static ToolOutcome MoveToStage(AgentTurnContext ctx, JsonElement input)
    {
        var stageName = input.GetProperty("stage").GetString();
        if (!StageNames.TryParse(stageName, out var target))
            return new ToolOutcome($"Неизвестная стадия {stageName}.", true);

        if (target == LeadStage.Lost && input.TryGetProperty("lost_reason", out var lr))
            ctx.Lead.Profile.LostReason = lr.GetString();

        var reason = input.TryGetProperty("reason", out var r) ? r.GetString() : null;
        var result = ctx.Lead.MoveTo(target, TransitionSource.Bot, ctx.Now, reason);
        if (!result.Success)
            return new ToolOutcome(result.Error!, true);

        if (result.From != result.To) ctx.StageChanged = true;
        return new ToolOutcome($"Стадия теперь {StageNames.Name(target)}. Цель: {StageNames.Goal(target)}");
    }

    private async Task<ToolOutcome> GetSlotsAsync(CancellationToken ct)
    {
        var slots = await calendar.GetFreeSlotsAsync(6, ct);
        return slots.Count == 0
            ? new ToolOutcome("Свободных слотов нет. Предложи клиенту передать вопрос менеджеру (handoff_to_human).", true)
            : new ToolOutcome("Свободные слоты: " + string.Join("; ", slots.Select(s => s.ToString("yyyy-MM-ddTHH:mmzzz", CultureInfo.InvariantCulture))));
    }

    private async Task<ToolOutcome> BookCallAsync(AgentTurnContext ctx, JsonElement input, CancellationToken ct)
    {
        var raw = input.GetProperty("slot").GetString();
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var slot))
            return new ToolOutcome("Не удалось разобрать время слота. Используй значение из get_call_slots.", true);
        if (slot <= ctx.Now)
            return new ToolOutcome("Слот в прошлом.", true);

        var lead = ctx.Lead;
        var previous = lead.Profile.CallSlot;
        lead.Profile.CallSlot = slot;

        var check = Domain.Funnel.FunnelStateMachine.Check(lead,
            LeadStage.CallScheduled, TransitionSource.Bot);
        // Перенос созвона: уже в CallScheduled — проверка вернёт Ok.
        if (!check.Success)
        {
            lead.Profile.CallSlot = previous;
            return new ToolOutcome(check.Error!, true);
        }

        if (!await calendar.TryBookAsync(slot, lead, ct))
        {
            lead.Profile.CallSlot = previous;
            return new ToolOutcome("Слот уже занят. Получи актуальные слоты через get_call_slots.", true);
        }

        lead.MoveTo(LeadStage.CallScheduled, TransitionSource.Bot, ctx.Now, $"созвон {slot:dd.MM HH:mm}");
        ctx.StageChanged = ctx.ProfileChanged = true;

        crm.CreateTask(lead.Id, $"Созвон-бриф с клиентом {lead.Profile.Name} ({lead.Identity.Channel})", slot);
        await notifier.NotifyAsync(lead, $"📅 Назначен созвон на {slot:dd.MM HH:mm}", urgent: false, ct);

        return new ToolOutcome($"Созвон записан на {slot:yyyy-MM-ddTHH:mmzzz}. Подтверди клиенту дату и время, скажи, что пришлём ссылку.");
    }

    private async Task<ToolOutcome> HandoffAsync(AgentTurnContext ctx, JsonElement input, CancellationToken ct)
    {
        var reason = input.GetProperty("reason").GetString() ?? "без причины";
        var urgent = input.TryGetProperty("urgent", out var u) && u.ValueKind == JsonValueKind.True;

        ctx.Lead.TakeoverByHuman(ctx.Now.AddHours(options.Value.HumanTakeoverHours), ctx.Now);
        ctx.Lead.AddSystemNote($"Передано человеку: {reason}", ctx.Now);
        ctx.HandedOff = true;

        crm.CreateTask(ctx.Lead.Id, $"Бот передал диалог: {reason}", ctx.Now.AddMinutes(urgent ? 15 : 120));
        await notifier.NotifyAsync(ctx.Lead, $"🙋 Нужен человек: {reason}", urgent, ct);

        return new ToolOutcome("Диалог передан сотруднику. Коротко скажи клиенту, что подключится коллега, и больше ничего не спрашивай.");
    }

    private static ToolOutcome ScheduleFollowUp(AgentTurnContext ctx, JsonElement input)
    {
        var hours = input.GetProperty("in_hours").GetInt32();
        if (hours is < 1 or > 2160)
            return new ToolOutcome("in_hours должно быть от 1 до 2160.", true);

        ctx.Lead.ScheduleFollowUp(ctx.Now.AddHours(hours));
        if (input.TryGetProperty("note", out var note))
            ctx.Lead.AddSystemNote($"Напомнить: {note.GetString()}", ctx.Now);
        ctx.FollowUpScheduled = true;
        return new ToolOutcome($"Напоминание запланировано через {hours} ч.");
    }

    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();
}
