using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Tests;

/// <summary>Revisa el modelo de EF Core sin conectarse a SQL Server.</summary>
public sealed class PersistenceModelTests
{
    private static WaBulkDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<WaBulkDbContext>()
            .UseSqlServer("Server=unused;Database=unused")
            .Options);

    [Fact]
    public void SendAttempt_Wamid_HasUniqueFilteredIndex()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(SendAttempt))!;

        var index = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(SendAttempt.Wamid)]));

        Assert.True(index.IsUnique);
        Assert.Equal("[Wamid] IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void UniqueIndexes_OnNullableColumns_AreFiltered()
    {
        // Regla 11 de CLAUDE.md, aplicada a todo el modelo.
        using var db = CreateContext();

        var offenders = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetIndexes())
            .Where(i => i.IsUnique && i.Properties.Any(p => p.IsNullable) && string.IsNullOrEmpty(i.GetFilter()))
            .Select(i => $"{i.DeclaringEntityType.GetTableName()}({string.Join(",", i.Properties.Select(p => p.Name))})")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Model_MapsTheExpectedTables()
    {
        using var db = CreateContext();

        var tables = db.Model.GetEntityTypes().Select(e => e.GetTableName() ?? "").Order().ToArray();

        // F0.4 + F0.10 (ConsentRecord, PlatformPreference) + F0.11 (CapacityReservation) + F0.12 (Alert, DispatchPause)
        // + F1.2 (tablas AspNet* de Identity) + F1.3 (AuditLog) + F1.4 (Contact, InboundMessage, ConsentOffer, Segment, SegmentMember).
        Assert.Equal<string>(
            ["Alert", "AspNetRoleClaims", "AspNetRoles", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserRoles",
             "AspNetUsers", "AspNetUserTokens", "AuditLog", "CapacityReservation", "ConsentOffer", "ConsentRecord", "Contact",
             "DispatchJob", "DispatchPause", "InboundMessage", "MessageEvent", "PlatformPreference", "Segment", "SegmentMember",
             "SendAttempt", "WebhookInbox"],
            tables);
    }
}
