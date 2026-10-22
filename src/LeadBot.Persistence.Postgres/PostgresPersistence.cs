using System.Text.Json;
using LeadBot.Application.Abstractions;
using LeadBot.Domain.Leads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LeadBot.Persistence.Postgres;

// Prod-хранилище. Схема намеренно простая: «горячие» поля для запросов — колонками,
// профиль/идентичность/история — jsonb (их не фильтруем, а читаем целиком вместе с лидом).
// TODO: при DbUpdateConcurrencyException (xmin) — перечитать лид и повторить операцию.
// После restore пакетов: dotnet ef migrations add Init -p src/LeadBot.Persistence.Postgres -s src/LeadBot.Api

public sealed class LeadBotDbContext(DbContextOptions<LeadBotDbContext> options) : DbContext(options)
{
    public DbSet<Lead> Leads => Set<Lead>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var b = model.Entity<Lead>();
        b.ToTable("leads");
        b.HasKey(l => l.Id);

        // Ключ поиска «канал:userId» — теневое свойство с уникальным индексом.
        b.Property<string>("IdentityKey").HasMaxLength(128);
        b.HasIndex("IdentityKey").IsUnique();

        b.Property(l => l.Stage).HasConversion<string>().HasMaxLength(32);
        b.Property(l => l.Mode).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(l => l.CrmLeadId);
        b.HasIndex(l => l.NextFollowUpAt);

        Json(b.Property(l => l.Identity));
        Json(b.Property(l => l.Profile));
        Json(b.Property<List<ChatMessage>>("_history").HasColumnName("history"));
        b.Ignore(l => l.History);
        b.Ignore(l => l.IsClosed);

        // Оптимистичная блокировка по системной колонке xmin (Npgsql).
        b.Property<uint>("Version").IsRowVersion();
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static void Json<T>(PropertyBuilder<T> p) where T : class
    {
        p.HasColumnType("jsonb")
         .HasConversion(
             v => JsonSerializer.Serialize(v, JsonOpts),
             v => JsonSerializer.Deserialize<T>(v, JsonOpts)!,
             new ValueComparer<T>(
                 (a, c) => JsonSerializer.Serialize(a, JsonOpts) == JsonSerializer.Serialize(c, JsonOpts),
                 v => JsonSerializer.Serialize(v, JsonOpts).GetHashCode(),
                 v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOpts), JsonOpts)!));
    }
}

public sealed class EfLeadRepository(LeadBotDbContext db) : ILeadRepository
{
    public Task<Lead?> FindByIdentityAsync(ChannelKind channel, string userId, CancellationToken ct)
    {
        var key = $"{channel}:{userId}";
        return db.Leads.FirstOrDefaultAsync(l => EF.Property<string>(l, "IdentityKey") == key, ct);
    }

    public Task<Lead?> FindByIdAsync(Guid id, CancellationToken ct) =>
        db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);

    public Task<Lead?> FindByCrmLeadIdAsync(long crmLeadId, CancellationToken ct) =>
        db.Leads.FirstOrDefaultAsync(l => l.CrmLeadId == crmLeadId, ct);

    public async Task<IReadOnlyList<Lead>> GetDueFollowUpsAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        await db.Leads.AsNoTracking()
            .Where(l => l.NextFollowUpAt != null && l.NextFollowUpAt <= now && l.Mode == ConversationMode.Bot
                        && l.Stage != LeadStage.Won && l.Stage != LeadStage.Lost)
            .OrderBy(l => l.NextFollowUpAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task SaveAsync(Lead lead, CancellationToken ct)
    {
        var entry = db.Entry(lead);
        if (entry.State == EntityState.Detached)
        {
            var exists = await db.Leads.AnyAsync(l => l.Id == lead.Id, ct);
            entry.State = exists ? EntityState.Modified : EntityState.Added;
        }
        entry.Property("IdentityKey").CurrentValue = lead.Identity.Key;
        await db.SaveChangesAsync(ct);
    }

    public Task SetCrmIdsAsync(Guid leadId, long crmLeadId, long? crmContactId, CancellationToken ct) =>
        db.Leads.Where(l => l.Id == leadId).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.CrmLeadId, crmLeadId)
            .SetProperty(l => l.CrmContactId, crmContactId), ct);
}

public static class PostgresExtensions
{
    /// <summary>Вызывать ДО AddLeadBotInfrastructure (та регистрирует InMemory через TryAdd).</summary>
    public static IServiceCollection AddPostgresPersistence(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<LeadBotDbContext>(o => o.UseNpgsql(config.GetConnectionString("LeadBot")));
        services.AddScoped<ILeadRepository, EfLeadRepository>();
        return services;
    }
}
