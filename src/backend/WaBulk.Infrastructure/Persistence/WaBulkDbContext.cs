using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Alerts;
using WaBulk.Domain.Capacity;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;

namespace WaBulk.Infrastructure.Persistence;

public class WaBulkDbContext(DbContextOptions<WaBulkDbContext> options) : DbContext(options)
{
    public DbSet<WebhookInbox> WebhookInbox => Set<WebhookInbox>();
    public DbSet<DispatchJob> DispatchJobs => Set<DispatchJob>();
    public DbSet<SendAttempt> SendAttempts => Set<SendAttempt>();
    public DbSet<MessageEvent> MessageEvents => Set<MessageEvent>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<PlatformPreference> PlatformPreferences => Set<PlatformPreference>();
    public DbSet<CapacityReservation> CapacityReservations => Set<CapacityReservation>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<DispatchPause> DispatchPauses => Set<DispatchPause>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WaBulkDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Todas las fechas se guardan en UTC como datetime2.
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2");
    }
}
