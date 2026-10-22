using LeadBot.Application.Abstractions;
using LeadBot.Application.Conversation;
using LeadBot.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LeadBot.Application;

/// <summary>Разбирает очередь входящих: каждый лид — в своей задаче, с ограничением параллелизма.</summary>
public sealed class IncomingMessageWorker(
    IncomingMessageQueue queue,
    IServiceScopeFactory scopes,
    ILogger<IncomingMessageWorker> log) : BackgroundService
{
    private readonly SemaphoreSlim _parallelism = new(32);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var msg in queue.ReadAllAsync(stoppingToken))
        {
            await _parallelism.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var orchestrator = scope.ServiceProvider.GetRequiredService<ConversationOrchestrator>();
                    await orchestrator.HandleIncomingAsync(msg, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    log.LogError(ex, "Ошибка обработки сообщения {ExternalId} от {Key}", msg.ExternalId, msg.From.Key);
                }
                finally
                {
                    _parallelism.Release();
                }
            }, stoppingToken);
        }
    }
}

/// <summary>Раз в минуту ищет лидов, которым пора напомнить о себе.</summary>
public sealed class FollowUpWorker(
    IServiceScopeFactory scopes,
    IClock clock,
    ILogger<FollowUpWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<ILeadRepository>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ConversationOrchestrator>();

                foreach (var lead in await repo.GetDueFollowUpsAsync(clock.Now, 50, stoppingToken))
                    await orchestrator.RunFollowUpAsync(lead.Id, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Ошибка в FollowUpWorker");
            }
        }
    }
}
