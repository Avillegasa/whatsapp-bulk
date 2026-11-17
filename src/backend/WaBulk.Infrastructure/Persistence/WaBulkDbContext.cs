using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Alerts;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Capacity;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Identity;

namespace WaBulk.Infrastructure.Persistence;

/// <summary>
/// Contexto único: tablas del sistema + tablas de Identity (AspNet*) en la misma base, para que un cambio de usuario
/// y su AuditLog (F1.3) se guarden en la misma transacción.
/// </summary>
public class WaBulkDbContext(DbContextOptions<WaBulkDbContext> options) : IdentityDbContext<AppUser>(options)
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
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // tablas de Identity
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WaBulkDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Todas las fechas se guardan en UTC como datetime2.
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2");
    }
}
