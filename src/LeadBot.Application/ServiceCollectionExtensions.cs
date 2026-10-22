using LeadBot.Application.Conversation;
using LeadBot.Application.Crm;
using LeadBot.Application.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LeadBot.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>Ядро: оркестратор, агент, очереди и фоновые воркеры. Каналы, CRM, LLM и хранилище подключаются отдельно.</summary>
    public static IServiceCollection AddLeadBotCore(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<FunnelOptions>(config.GetSection(FunnelOptions.Section));

        services.AddSingleton<IncomingMessageQueue>();
        services.AddSingleton<CrmSyncQueue>();
        services.AddSingleton<KeyedLock>();
        services.AddSingleton<DebounceTracker>();
        services.AddSingleton<PromptBuilder>();
        services.AddScoped<ChannelSenderRegistry>();

        services.AddScoped<AgentToolbox>();
        services.AddScoped<ConversationOrchestrator>();

        services.AddHostedService<IncomingMessageWorker>();
        services.AddHostedService<CrmSyncWorker>();
        services.AddHostedService<FollowUpWorker>();
        return services;
    }
}
