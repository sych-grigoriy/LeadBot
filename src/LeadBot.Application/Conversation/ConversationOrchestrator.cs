using System.Collections.Concurrent;
using LeadBot.Application.Abstractions;
using LeadBot.Application.Crm;
using LeadBot.Application.Messaging;
using LeadBot.Domain.Leads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadBot.Application.Conversation;

/// <summary>Отправители по каналам (Telegram, MAX).</summary>
public sealed class ChannelSenderRegistry(IEnumerable<IChannelSender> senders)
{
    private readonly Dictionary<ChannelKind, IChannelSender> _map = senders.ToDictionary(s => s.Channel);

    public IChannelSender For(ChannelKind channel) =>
        _map.TryGetValue(channel, out var s) ? s : throw new InvalidOperationException($"Канал {channel} не подключён");
}

/// <summary>Счётчик версий для «склейки» очереди сообщений клиента в один ответ.</summary>
public sealed class DebounceTracker
{
    private readonly ConcurrentDictionary<string, long> _versions = new();
    public long Bump(string key) => _versions.AddOrUpdate(key, 1, (_, v) => v + 1);
    public bool IsLatest(string key, long version) => _versions.TryGetValue(key, out var v) && v == version;
}

/// <summary>
/// Сердце бота: принимает нормализованные сообщения из любого канала, ведёт лид по воронке
/// через LLM-агента и машину состояний, отвечает в тот же канал и ставит синхронизацию в CRM.
/// </summary>
public sealed class ConversationOrchestrator(
    ILeadRepository repo,
    ILlmClient llm,
    AgentToolbox toolbox,
    PromptBuilder prompts,
    ChannelSenderRegistry senders,
    CrmSyncQueue crm,
    IOwnerNotifier notifier,
    IClock clock,
    KeyedLock locks,
    DebounceTracker debounce,
    IOptions<FunnelOptions> options,
    ILogger<ConversationOrchestrator> log)
{
    private readonly FunnelOptions _opt = options.Value;

    public async Task HandleIncomingAsync(IncomingMessage msg, CancellationToken ct)
    {
        var key = msg.From.Key;
        long version;

        using (await locks.AcquireAsync(key, ct))
        {
            var now = clock.Now;
            var lead = await repo.FindByIdentityAsync(msg.From.Channel, msg.From.UserId, ct);
            var isNew = lead is null;
            lead ??= new Lead(msg.From, now);
            lead.RefreshIdentity(msg.From);

            if (lead.HasMessage(msg.ExternalId))
                return; // повторная доставка вебхука

            lead.ExpireTakeoverIfNeeded(now);

            if (msg.Kind == IncomingKind.ManagerReply)
            {
                // Владелец сам пишет в чат, где лида нет (друзья, коллеги) — не создаём лид.
                if (isNew) return;

                // Владелец ответил сам из своего Telegram — бот уступает диалог.
                lead.AddManagerMessage(msg.Text, msg.At, msg.ExternalId);
                lead.TakeoverByHuman(now.AddHours(_opt.HumanTakeoverHours), now);
                await repo.SaveAsync(lead, ct);
                crm.AddNote(lead.Id, $"Менеджер: {msg.Text}");
                return;
            }

            if (msg.Kind == IncomingKind.Start)
            {
                lead.Profile.Source ??= msg.StartPayload;
                lead.AddClientMessage(
                    string.IsNullOrWhiteSpace(msg.StartPayload)
                        ? "(клиент открыл чат с ботом)"
                        : $"(клиент открыл чат с ботом, источник: {msg.StartPayload})",
                    msg.At, msg.ExternalId);
            }
            else
            {
                lead.AddClientMessage(msg.Text, msg.At, msg.ExternalId);
            }

            if (isNew)
                lead.Profile.Source ??= msg.From.BusinessConnectionId is not null
                    ? "telegram-business"
                    : msg.From.Channel.ToString().ToLowerInvariant();

            // Новый или вернувшийся клиент — в квалификацию.
            if (lead.Stage is LeadStage.New or LeadStage.Nurturing or LeadStage.Lost)
                lead.MoveTo(LeadStage.Qualifying, TransitionSource.System, now, "клиент написал");

            await repo.SaveAsync(lead, ct);
            if (isNew) crm.UpsertLead(lead.Id);

            if (lead.Mode == ConversationMode.Human)
            {
                crm.AddNote(lead.Id, $"Клиент (бот на паузе): {msg.Text}");
                return;
            }

            version = debounce.Bump(key);
        }

        // Ждём: вдруг клиент допишет ещё пару сообщений — ответим на всё сразу.
        await Task.Delay(TimeSpan.FromSeconds(_opt.DebounceSeconds), ct);

        using (await locks.AcquireAsync(key, ct))
        {
            if (!debounce.IsLatest(key, version))
                return; // более позднее сообщение ответит за всех

            var lead = await repo.FindByIdentityAsync(msg.From.Channel, msg.From.UserId, ct);
            if (lead is null || lead.Mode != ConversationMode.Bot)
                return;

            await RunAgentTurnAsync(lead, extraInstruction: null, syntheticUserTurn: null, ct);
        }
    }

    /// <summary>Напоминание клиенту, который замолчал.</summary>
    public async Task RunFollowUpAsync(Guid leadId, CancellationToken ct)
    {
        var lead = await repo.FindByIdAsync(leadId, ct);
        if (lead is null) return;

        using (await locks.AcquireAsync(lead.Identity.Key, ct))
        {
            lead = await repo.FindByIdAsync(leadId, ct);
            var now = clock.Now;
            if (lead is null || lead.IsClosed || lead.NextFollowUpAt is null || lead.NextFollowUpAt > now)
                return;

            lead.ExpireTakeoverIfNeeded(now);
            if (lead.Mode != ConversationMode.Bot)
                return;

            if (IsQuietHours(now, out var resumeAt))
            {
                lead.ScheduleFollowUp(resumeAt);
                await repo.SaveAsync(lead, ct);
                return;
            }

            if (lead.FollowUpCount >= _opt.MaxFollowUps)
            {
                lead.MoveTo(LeadStage.Nurturing, TransitionSource.System, now, "клиент не отвечает");
                lead.RegisterFollowUpSent(now); // снимаем расписание
                await repo.SaveAsync(lead, ct);
                crm.UpsertLead(lead.Id);
                return;
            }

            lead.RegisterFollowUpSent(now);
            await RunAgentTurnAsync(lead,
                extraInstruction:
                    "Клиент давно не отвечает. Напиши одно короткое ненавязчивое сообщение: напомни, на чём остановились, " +
                    "и задай один простой вопрос, чтобы продолжить. Без давления и без повторения прошлых сообщений дословно.",
                syntheticUserTurn: $"(клиент не отвечает с {lead.LastClientMessageAt:dd.MM HH:mm})",
                ct);
        }
    }

    /// <summary>Стадию поменяли в amoCRM — отражаем у себя.</summary>
    public async Task ApplyCrmStageAsync(long crmLeadId, LeadStage stage, CancellationToken ct)
    {
        var lead = await repo.FindByCrmLeadIdAsync(crmLeadId, ct);
        if (lead is null) return;

        using (await locks.AcquireAsync(lead.Identity.Key, ct))
        {
            lead = await repo.FindByIdAsync(lead.Id, ct);
            if (lead is null) return;
            var result = lead.MoveTo(stage, TransitionSource.Crm, clock.Now, "изменено в amoCRM");
            if (result.From != result.To)
                await repo.SaveAsync(lead, ct);
        }
    }

    public async Task SetModeAsync(Guid leadId, ConversationMode mode, CancellationToken ct)
    {
        var lead = await repo.FindByIdAsync(leadId, ct);
        if (lead is null) return;

        using (await locks.AcquireAsync(lead.Identity.Key, ct))
        {
            lead = await repo.FindByIdAsync(leadId, ct);
            if (lead is null) return;
            var now = clock.Now;
            if (mode == ConversationMode.Bot) lead.ReturnToBot(now);
            else lead.TakeoverByHuman(now.AddHours(_opt.HumanTakeoverHours), now);
            await repo.SaveAsync(lead, ct);
        }
    }

    // Вызывается под блокировкой лида.
    private async Task RunAgentTurnAsync(Lead lead, string? extraInstruction, string? syntheticUserTurn, CancellationToken ct)
    {
        var now = clock.Now;
        var ctx = new AgentTurnContext(lead, now);
        var sender = senders.For(lead.Identity.Channel);
        var lastClientText = lead.History.LastOrDefault(m => m.Author == MessageAuthor.Client)?.Text;

        string reply;
        try
        {
            await SafeTypingAsync(sender, lead, ct);

            var system = prompts.BuildSystemPrompt(lead, now, extraInstruction);
            var messages = prompts.BuildHistory(lead, syntheticUserTurn);
            reply = "";

            for (var step = 0; step < _opt.MaxAgentSteps; step++)
            {
                var response = await llm.CompleteAsync(new LlmRequest(system, messages, toolbox.Definitions), ct);
                var calls = response.ToolCalls.ToList();

                if (calls.Count == 0)
                {
                    reply = response.Text;
                    break;
                }

                messages.Add(new LlmMessage(LlmRole.Assistant, response.Content));
                var results = new List<LlmContent>();
                foreach (var call in calls)
                {
                    var outcome = await toolbox.ExecuteAsync(ctx, call, ct);
                    log.LogInformation("Lead {LeadId} tool {Tool} → {Result}", lead.Id, call.Name, outcome.Content);
                    results.Add(new LlmToolResult(call.Id, outcome.Content, outcome.IsError));
                }
                messages.Add(new LlmMessage(LlmRole.User, results));

                // Состояние лида изменилось — обновим системный промпт, чтобы модель видела новую стадию/данные.
                system = prompts.BuildSystemPrompt(lead, now, extraInstruction);
            }

            if (string.IsNullOrWhiteSpace(reply))
                reply = ctx.HandedOff
                    ? "Спасибо! Передал ваш вопрос коллеге — он скоро подключится к диалогу."
                    : _opt.FallbackReply;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Ошибка агента для лида {LeadId}", lead.Id);
            reply = _opt.FallbackReply;
            if (!ctx.HandedOff)
            {
                lead.TakeoverByHuman(now.AddHours(_opt.HumanTakeoverHours), now);
                ctx.HandedOff = true;
                await notifier.NotifyAsync(lead, "⚠️ Бот не смог ответить (ошибка LLM), диалог передан вам.", urgent: true, ct);
            }
        }

        try
        {
            await sender.SendTextAsync(lead.Identity, reply, ct);
            lead.AddBotMessage(reply, clock.Now);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Не удалось отправить ответ лиду {LeadId}", lead.Id);
            await notifier.NotifyAsync(lead, $"⚠️ Не удалось отправить ответ клиенту: {ex.Message}", urgent: true, ct);
        }

        // Автонапоминание, если клиент замолчит, пока лид ещё не дошёл до созвона.
        if (!ctx.FollowUpScheduled && lead.Mode == ConversationMode.Bot && !lead.IsClosed
            && lead.Stage < LeadStage.CallScheduled && lead.FollowUpCount < _opt.MaxFollowUps)
        {
            lead.ScheduleFollowUp(clock.Now.AddHours(_opt.FollowUpAfterHours));
        }

        await repo.SaveAsync(lead, ct);

        if (ctx.ProfileChanged || ctx.StageChanged || ctx.HandedOff)
            crm.UpsertLead(lead.Id);
        crm.AddNote(lead.Id, lastClientText is null || syntheticUserTurn is not null
            ? $"Бот: {reply}"
            : $"Клиент: {lastClientText}\nБот: {reply}");
    }

    private async Task SafeTypingAsync(IChannelSender sender, Lead lead, CancellationToken ct)
    {
        try { await sender.SendTypingAsync(lead.Identity, ct); }
        catch (Exception ex) { log.LogDebug(ex, "typing не отправлен"); }
    }

    private bool IsQuietHours(DateTimeOffset now, out DateTimeOffset resumeAt)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(_opt.TimeZone);
        var local = TimeZoneInfo.ConvertTime(now, tz);
        var h = local.Hour;
        var quiet = _opt.QuietHoursFrom > _opt.QuietHoursTo
            ? h >= _opt.QuietHoursFrom || h < _opt.QuietHoursTo
            : h >= _opt.QuietHoursFrom && h < _opt.QuietHoursTo;

        var resumeLocal = local.Date.AddHours(_opt.QuietHoursTo);
        if (resumeLocal <= local.DateTime) resumeLocal = resumeLocal.AddDays(1);
        resumeAt = new DateTimeOffset(resumeLocal, tz.GetUtcOffset(resumeLocal));
        return quiet;
    }
}
