using System.Threading.Channels;
using LeadBot.Application.Abstractions;
using LeadBot.Domain.Leads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LeadBot.Application.Crm;

/// <summary>Порт CRM. Реализация — amoCRM; при желании можно добавить Bitrix24 с тем же контрактом.</summary>
public interface ICrmGateway
{
    /// <summary>Создать или обновить сделку + контакт. Возвращает ID в CRM.</summary>
    Task<(long LeadId, long? ContactId)> UpsertLeadAsync(Lead lead, CancellationToken ct);
    Task AddNoteAsync(long crmLeadId, string text, CancellationToken ct);
    Task CreateTaskAsync(long crmLeadId, string text, DateTimeOffset dueAt, CancellationToken ct);
}

public enum CrmCommandKind { UpsertLead, AddNote, CreateTask }

public sealed record CrmCommand(CrmCommandKind Kind, Guid LeadId, string? Text = null, DateTimeOffset? DueAt = null, int Attempt = 0);

/// <summary>
/// Очередь синхронизации с CRM. Диалог с клиентом никогда не ждёт CRM: если amoCRM лежит,
/// клиент всё равно получает ответ, а синхронизация догонит позже.
/// В проде заменить на таблицу Outbox в той же транзакции, что и сохранение лида.
/// </summary>
public sealed class CrmSyncQueue
{
    private readonly Channel<CrmCommand> _channel = Channel.CreateUnbounded<CrmCommand>();

    public void Enqueue(CrmCommand command) => _channel.Writer.TryWrite(command);
    public void UpsertLead(Guid leadId) => Enqueue(new CrmCommand(CrmCommandKind.UpsertLead, leadId));
    public void AddNote(Guid leadId, string text) => Enqueue(new CrmCommand(CrmCommandKind.AddNote, leadId, text));
    public void CreateTask(Guid leadId, string text, DateTimeOffset dueAt) =>
        Enqueue(new CrmCommand(CrmCommandKind.CreateTask, leadId, text, dueAt));

    internal IAsyncEnumerable<CrmCommand> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class CrmSyncWorker(
    CrmSyncQueue queue,
    IServiceScopeFactory scopes,
    ILogger<CrmSyncWorker> log) : BackgroundService
{
    private const int MaxAttempts = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var cmd in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await HandleAsync(cmd, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (cmd.Attempt + 1 >= MaxAttempts)
                {
                    log.LogError(ex, "CRM sync окончательно не удался: {Command}", cmd);
                    continue;
                }
                var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, cmd.Attempt + 1)));
                log.LogWarning(ex, "CRM sync ошибка, повтор через {Delay}: {Command}", delay, cmd);
                _ = RequeueLaterAsync(cmd with { Attempt = cmd.Attempt + 1 }, delay, stoppingToken);
            }
        }
    }

    private async Task RequeueLaterAsync(CrmCommand cmd, TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); queue.Enqueue(cmd); }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(CrmCommand cmd, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILeadRepository>();
        var crm = scope.ServiceProvider.GetRequiredService<ICrmGateway>();

        var lead = await repo.FindByIdAsync(cmd.LeadId, ct);
        if (lead is null) return;

        // Заметки и задачи требуют, чтобы сделка уже существовала в CRM.
        if (lead.CrmLeadId is null && cmd.Kind != CrmCommandKind.UpsertLead)
        {
            await UpsertAsync(lead, repo, crm, ct);
        }

        switch (cmd.Kind)
        {
            case CrmCommandKind.UpsertLead:
                await UpsertAsync(lead, repo, crm, ct);
                break;
            case CrmCommandKind.AddNote:
                await crm.AddNoteAsync(lead.CrmLeadId!.Value, cmd.Text ?? "", ct);
                break;
            case CrmCommandKind.CreateTask:
                await crm.CreateTaskAsync(lead.CrmLeadId!.Value, cmd.Text ?? "", cmd.DueAt ?? DateTimeOffset.UtcNow.AddHours(1), ct);
                break;
        }
    }

    private static async Task UpsertAsync(Lead lead, ILeadRepository repo, ICrmGateway crm, CancellationToken ct)
    {
        var (crmLeadId, contactId) = await crm.UpsertLeadAsync(lead, ct);
        if (lead.CrmLeadId != crmLeadId || (contactId is not null && lead.CrmContactId != contactId))
        {
            lead.CrmLeadId = crmLeadId;
            lead.CrmContactId = contactId ?? lead.CrmContactId;
            // Точечное обновление, чтобы не затереть параллельные изменения диалога.
            await repo.SetCrmIdsAsync(lead.Id, crmLeadId, lead.CrmContactId, ct);
        }
    }
}
