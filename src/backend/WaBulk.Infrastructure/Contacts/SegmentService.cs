using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Contacts;
using WaBulk.Infrastructure.Audit;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Contacts;

public sealed record SegmentSummary(long Id, string Name, string? Description, int Members, DateTime CreatedUtc, string CreatedBy);

/// <summary>Listas estáticas de contactos (Segment, SegmentMember). Cada cambio con su AuditLog en la misma transacción.</summary>
public sealed class SegmentService(WaBulkDbContext db, TimeProvider clock)
{
    public Task<List<SegmentSummary>> ListAsync(CancellationToken cancellationToken) =>
        db.Segments.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new SegmentSummary(s.Id, s.Name, s.Description,
                db.SegmentMembers.Count(m => m.SegmentId == s.Id), s.CreatedUtc, s.CreatedBy))
            .ToListAsync(cancellationToken);

    public Task<bool> ExistsAsync(long id, CancellationToken cancellationToken) =>
        db.Segments.AnyAsync(s => s.Id == id, cancellationToken);

    public async Task<(ContactActionResult Result, SegmentSummary? Segment)> CreateAsync(
        string actor, string? name, string? description, CancellationToken cancellationToken)
    {
        var n = name?.Trim() ?? "";
        if (n.Length is 0 or > 100)
        {
            return (ContactActionResult.Fail("NombreInvalido", "El nombre de la lista debe tener entre 1 y 100 caracteres."), null);
        }

        if (await db.Segments.AnyAsync(s => s.Name == n, cancellationToken))
        {
            return (ContactActionResult.Fail("NombreRepetido", $"Ya existe una lista llamada \"{n}\"."), null);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var segment = new Segment { Name = n, Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(), CreatedUtc = now, CreatedBy = actor };
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Segments.Add(segment);
        await db.SaveChangesAsync(cancellationToken);
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.CrearLista, "Lista", segment.Id.ToString(CultureInfo.InvariantCulture),
            null, new { nombre = segment.Name, descripcion = segment.Description }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return (ContactActionResult.Ok, new SegmentSummary(segment.Id, segment.Name, segment.Description, 0, now, actor));
    }

    /// <summary>Agrega contactos a la lista; los que ya estaban se ignoran. Devuelve cuántos se agregaron.</summary>
    public async Task<(ContactActionResult Result, int Added)> AddMembersAsync(
        string actor, long segmentId, IReadOnlyCollection<long> contactIds, CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(segmentId, cancellationToken))
        {
            return (ContactActionResult.Fail("NoExiste", "La lista no existe."), 0);
        }

        var ids = contactIds.Distinct().ToList();
        if (ids.Count is 0 or > 1000)
        {
            return (ContactActionResult.Fail("SinContactos", "Elige entre 1 y 1000 contactos."), 0);
        }

        var existing = await db.Contacts.Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken);
        if (existing.Count != ids.Count)
        {
            return (ContactActionResult.Fail("ContactoNoExiste", "Algún contacto elegido no existe."), 0);
        }

        var already = await db.SegmentMembers.Where(m => m.SegmentId == segmentId && ids.Contains(m.ContactId))
            .Select(m => m.ContactId).ToListAsync(cancellationToken);
        var toAdd = ids.Except(already).ToList();
        if (toAdd.Count == 0)
        {
            return (ContactActionResult.Ok, 0);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.SegmentMembers.AddRange(toAdd.Select(id => new SegmentMember { SegmentId = segmentId, ContactId = id, AddedUtc = now, AddedBy = actor }));
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.AgregarALista, "Lista", segmentId.ToString(CultureInfo.InvariantCulture),
            null, new { contactos = toAdd }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return (ContactActionResult.Ok, toAdd.Count);
    }

    public async Task<ContactActionResult> RemoveMemberAsync(string actor, long segmentId, long contactId, CancellationToken cancellationToken)
    {
        var member = await db.SegmentMembers.SingleOrDefaultAsync(m => m.SegmentId == segmentId && m.ContactId == contactId, cancellationToken);
        if (member is null)
        {
            return ContactActionResult.Fail("NoExiste", "El contacto no está en la lista.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.SegmentMembers.Remove(member);
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.QuitarDeLista, "Lista", segmentId.ToString(CultureInfo.InvariantCulture),
            new { contacto = contactId }, null, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return ContactActionResult.Ok;
    }

    public Task<List<long>> MemberIdsAsync(long segmentId, CancellationToken cancellationToken) =>
        db.SegmentMembers.AsNoTracking().Where(m => m.SegmentId == segmentId).Select(m => m.ContactId).ToListAsync(cancellationToken);
}
