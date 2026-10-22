using LeadBot.Application;
using LeadBot.Application.Crm;
using LeadBot.Channels.Max;
using LeadBot.Channels.Telegram;
using LeadBot.Crm.AmoCrm;
using LeadBot.Infrastructure;
using LeadBot.Llm.Anthropic;
using LeadBot.Llm.DeepSeek;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services
    .AddLeadBotCore(config)
    // Каналы и CRM регистрируются ДО инфраструктуры: она добавляет заглушки через TryAdd.
    .AddTelegramChannel(config)
    .AddMaxChannel(config)
    .AddAmoCrm(config)
    // Prod: .AddPostgresPersistence(config) — до AddLeadBotInfrastructure
    .AddLeadBotInfrastructure(config);

// LLM-провайдер: Llm:Provider = DeepSeek | Anthropic
var llmProvider = config.GetValue("Llm:Provider", "DeepSeek")!;
if (llmProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddAnthropicLlm(config);
else if (llmProvider.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddDeepSeekLlm(config);
else
    throw new InvalidOperationException($"Неизвестный Llm:Provider '{llmProvider}'. Допустимо: DeepSeek, Anthropic.");

// Если amoCRM выключена — CRM-команды просто логируются.
if (!config.GetValue("AmoCrm:Enabled", true))
    builder.Services.AddScoped<ICrmGateway, NullCrmGateway>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

if (config.GetValue("Telegram:Enabled", true)) app.MapTelegramWebhook();
if (config.GetValue("Max:Enabled", true)) app.MapMaxWebhook();
if (config.GetValue("AmoCrm:Enabled", true)) app.MapAmoCrmWebhook();

app.Run();

/// <summary>CRM-заглушка для локальной разработки.</summary>
internal sealed class NullCrmGateway(ILogger<NullCrmGateway> log) : ICrmGateway
{
    private long _seq = 1000;

    public Task<(long LeadId, long? ContactId)> UpsertLeadAsync(LeadBot.Domain.Leads.Lead lead, CancellationToken ct)
    {
        var id = lead.CrmLeadId ?? Interlocked.Increment(ref _seq);
        log.LogInformation("[CRM] upsert {Id}: {Stage} {@Profile}", id, lead.Stage, lead.Profile);
        return Task.FromResult<(long, long?)>((id, null));
    }

    public Task AddNoteAsync(long crmLeadId, string text, CancellationToken ct)
    {
        log.LogInformation("[CRM] note {Id}: {Text}", crmLeadId, text);
        return Task.CompletedTask;
    }

    public Task CreateTaskAsync(long crmLeadId, string text, DateTimeOffset dueAt, CancellationToken ct)
    {
        log.LogInformation("[CRM] task {Id} до {Due}: {Text}", crmLeadId, dueAt, text);
        return Task.CompletedTask;
    }
}
