using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WaBulk.Domain.Audit;
using WaBulk.Domain.Consent;
using WaBulk.Domain.Contacts;
using WaBulk.Infrastructure.Audit;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Infrastructure.Contacts;

/// <summary>Resultado de una fila: Creado, Actualizado, Advertencia (se importó con una salvedad) o Error (no se importó).</summary>
public sealed record ImportRowResult(int Row, string? Phone, string Result, string Message);

public sealed record ImportReport(
    string FileName, int Rows, int Valid, int Errors, int Created, int Updated, int OptIns, int OptOuts, int Warnings,
    IReadOnlyList<ImportRowResult> Detail);

/// <summary>
/// Importación de contactos desde CSV o Excel (F1.4). Columnas (encabezado en la primera fila, sin importar mayúsculas
/// ni tildes): Telefono (obligatoria), Nombre, ClienteId, Consentimiento (vacío, OptIn u OptOut) y Evidencia.
/// Reglas: teléfono en E.164 (ver <see cref="PhoneNumbers"/>); un OptIn exige Evidencia; una importación nunca levanta
/// una baja (D-025: el OptIn sobre un OptOut no se registra y la fila queda como Advertencia). Todo en una transacción
/// con un solo AuditLog; las filas con error no frenan a las demás.
/// </summary>
public sealed class ContactImportService(WaBulkDbContext db, TimeProvider clock)
{
    public const int MaxRows = 5000;

    private static readonly string[] PhoneHeaders = ["TELEFONO", "CELULAR", "NUMERO", "PHONE"];
    private static readonly string[] NameHeaders = ["NOMBRE", "NAME"];
    private static readonly string[] CustomerHeaders = ["CLIENTEID", "CLIENTE", "CODIGO", "CODIGO CLIENTE"];
    private static readonly string[] ConsentHeaders = ["CONSENTIMIENTO"];
    private static readonly string[] EvidenceHeaders = ["EVIDENCIA"];

    /// <summary>Lee el archivo (.csv o .xlsx) en filas de texto. Lanza <see cref="InvalidDataException"/> si no se puede leer.</summary>
    public static List<string[]> ReadTable(Stream content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        var ext = Path.GetExtension(fileName).ToUpperInvariant();
        return ext switch
        {
            ".CSV" or ".TXT" => ReadCsv(content),
            ".XLSX" => ReadExcel(content),
            _ => throw new InvalidDataException("Formato no admitido: sube un archivo .csv o .xlsx."),
        };
    }

    public async Task<ImportReport> ImportAsync(
        string actor, string fileName, List<string[]> table, long? segmentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Count == 0)
        {
            throw new InvalidDataException("El archivo está vacío.");
        }

        var header = table[0].Select(OptOutDetector.Normalize).ToArray();
        int Col(string[] names) => Array.FindIndex(header, h => names.Contains(h) || names.Contains(h.Replace(" ", "", StringComparison.Ordinal)));
        var phoneCol = Col(PhoneHeaders);
        if (phoneCol < 0)
        {
            throw new InvalidDataException("Falta la columna Telefono en la primera fila.");
        }

        var (nameCol, customerCol, consentCol, evidenceCol) = (Col(NameHeaders), Col(CustomerHeaders), Col(ConsentHeaders), Col(EvidenceHeaders));
        var dataRows = table.Skip(1).Select((cells, i) => (Row: i + 2, Cells: cells))
            .Where(r => r.Cells.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (dataRows.Count > MaxRows)
        {
            throw new InvalidDataException($"El archivo tiene {dataRows.Count} filas; el máximo es {MaxRows}.");
        }

        string? Cell(string[] cells, int col) => col >= 0 && col < cells.Length && !string.IsNullOrWhiteSpace(cells[col]) ? cells[col].Trim() : null;

        // 1) Validación de cada fila (sin tocar la base).
        var results = new List<ImportRowResult>();
        var valid = new List<(int Row, string Phone, string? Name, string? Customer, ConsentStatus? Consent, string? Evidence)>();
        var seen = new Dictionary<string, int>();
        foreach (var (row, cells) in dataRows)
        {
            var rawPhone = Cell(cells, phoneCol);
            if (!PhoneNumbers.TryNormalize(rawPhone, out var phone, out var phoneError))
            {
                results.Add(new(row, rawPhone, "Error", phoneError));
                continue;
            }

            if (seen.TryGetValue(phone, out var firstRow))
            {
                results.Add(new(row, phone, "Error", $"Teléfono repetido (ya está en la fila {firstRow})."));
                continue;
            }

            var consentText = OptOutDetector.Normalize(Cell(cells, consentCol));
            ConsentStatus? consent = consentText switch
            {
                "" => null,
                "OPTIN" or "OPT IN" or "SI" => ConsentStatus.OptIn,
                "OPTOUT" or "OPT OUT" or "NO" or "BAJA" => ConsentStatus.OptOut,
                _ => (ConsentStatus)(-1),
            };
            if (consent == (ConsentStatus)(-1))
            {
                results.Add(new(row, phone, "Error", $"Consentimiento \"{Cell(cells, consentCol)}\" no válido: deja vacío, OptIn u OptOut."));
                continue;
            }

            var evidence = Cell(cells, evidenceCol);
            if (consent == ConsentStatus.OptIn && evidence is null)
            {
                results.Add(new(row, phone, "Error", "Un OptIn importado exige Evidencia (cómo y cuándo aceptó)."));
                continue;
            }

            var name = Cell(cells, nameCol);
            var customer = Cell(cells, customerCol);
            if (name?.Length > 200 || customer?.Length > 50 || evidence?.Length > 900)
            {
                results.Add(new(row, phone, "Error", "Texto demasiado largo (Nombre ≤ 200, ClienteId ≤ 50, Evidencia ≤ 900)."));
                continue;
            }

            seen[phone] = row;
            valid.Add((row, phone, name, customer, consent, evidence));
        }

        // 2) Estado actual de esos teléfonos (por tandas, por el límite de parámetros de SQL Server).
        var phones = valid.Select(v => v.Phone).ToList();
        var contacts = new Dictionary<string, Contact>();
        var current = new Dictionary<string, ConsentStatus>();
        foreach (var chunk in phones.Chunk(1000))
        {
            foreach (var c in await db.Contacts.Where(c => c.PhoneE164 != null && chunk.Contains(c.PhoneE164)).ToListAsync(cancellationToken))
            {
                contacts[c.PhoneE164!] = c;
            }

            var latest = await db.ConsentRecords.AsNoTracking()
                .Where(r => chunk.Contains(r.PhoneE164) && r.Purpose == ConsentPurposes.Promociones)
                .GroupBy(r => r.PhoneE164)
                .Select(g => g.OrderByDescending(r => r.RecordedUtc).ThenByDescending(r => r.Id).Select(r => new { r.PhoneE164, r.Status }).First())
                .ToListAsync(cancellationToken);
            foreach (var l in latest)
            {
                current[l.PhoneE164] = l.Status;
            }
        }

        // 3) Cambios, en una transacción con un solo AuditLog.
        var now = clock.GetUtcNow().UtcDateTime;
        var by = $"importacion:{actor}";
        int created = 0, updated = 0, optIns = 0, optOuts = 0, warnings = 0;
        var pendingConsents = new List<(Contact Contact, ConsentRecord Record)>();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var v in valid)
        {
            var isNew = !contacts.TryGetValue(v.Phone, out var contact);
            if (contact is null)
            {
                contact = new Contact { PhoneE164 = v.Phone, WaId = v.Phone.TrimStart('+'), CreatedUtc = now, CreatedBy = by };
                db.Contacts.Add(contact);
                contacts[v.Phone] = contact;
                created++;
            }
            else
            {
                updated++;
            }

            contact.Name = v.Name ?? contact.Name;
            contact.CustomerId = v.Customer ?? contact.CustomerId;

            var message = isNew ? "Contacto nuevo." : "Contacto actualizado.";
            var result = isNew ? "Creado" : "Actualizado";
            current.TryGetValue(v.Phone, out var cur);
            var hasCurrent = current.ContainsKey(v.Phone);
            if (v.Consent == ConsentStatus.OptIn)
            {
                if (hasCurrent && cur is ConsentStatus.OptOut or ConsentStatus.NoContactar)
                {
                    result = "Advertencia";
                    message += $" Tiene {cur}: el OptIn no se registró (una importación nunca levanta una baja).";
                    warnings++;
                }
                else if (hasCurrent && cur == ConsentStatus.OptIn)
                {
                    message += " Ya tenía OptIn.";
                }
                else
                {
                    pendingConsents.Add((contact, NewRecord(v.Phone, ConsentStatus.OptIn, v.Evidence, now, actor)));
                    message += " OptIn registrado.";
                    optIns++;
                }
            }
            else if (v.Consent == ConsentStatus.OptOut)
            {
                if (hasCurrent && cur is ConsentStatus.OptOut or ConsentStatus.NoContactar)
                {
                    message += " Ya tenía la baja.";
                }
                else
                {
                    pendingConsents.Add((contact, NewRecord(v.Phone, ConsentStatus.OptOut, v.Evidence, now, actor)));
                    message += " Baja registrada.";
                    optOuts++;
                }
            }

            results.Add(new(v.Row, v.Phone, result, message));
        }

        await db.SaveChangesAsync(cancellationToken); // Contactos nuevos con su Id.
        foreach (var (contact, record) in pendingConsents)
        {
            record.ContactId = contact.Id;
            db.ConsentRecords.Add(record);
        }

        if (segmentId is { } sid)
        {
            var ids = valid.Select(v => contacts[v.Phone].Id).ToList();
            var already = new HashSet<long>();
            foreach (var chunk in ids.Chunk(1000))
            {
                already.UnionWith(await db.SegmentMembers.Where(m => m.SegmentId == sid && chunk.Contains(m.ContactId))
                    .Select(m => m.ContactId).ToListAsync(cancellationToken));
            }

            db.SegmentMembers.AddRange(ids.Where(id => !already.Contains(id))
                .Select(id => new SegmentMember { SegmentId = sid, ContactId = id, AddedUtc = now, AddedBy = actor }));
        }

        var errors = results.Count(r => r.Result == "Error");
        var report = new ImportReport(fileName, dataRows.Count, valid.Count, errors, created, updated, optIns, optOuts, warnings,
            [.. results.OrderBy(r => r.Row)]);
        db.AuditLogs.Add(AuditEntry.Create(actor, AuditActions.ImportarContactos, "Importacion", Truncate(fileName, 100), null,
            new { archivo = fileName, lista = segmentId, filas = report.Rows, validas = report.Valid, errores = errors, creados = created,
                  actualizados = updated, optIn = optIns, optOut = optOuts, advertencias = warnings }, now));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return report;
    }

    private static ConsentRecord NewRecord(string phone, ConsentStatus status, string? evidence, DateTime now, string actor) => new()
    {
        PhoneE164 = phone,
        Status = status,
        Source = ConsentSource.Importacion,
        Evidence = evidence,
        RecordedUtc = now,
        RecordedBy = actor,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static List<string[]> ReadExcel(Stream content)
    {
        try
        {
            using var workbook = new XLWorkbook(content);
            var sheet = workbook.Worksheets.First();
            var used = sheet.RangeUsed();
            if (used is null)
            {
                return [];
            }

            var lastCol = used.LastColumn().ColumnNumber();
            return [.. used.Rows().Select(r => Enumerable.Range(1, lastCol).Select(i => CellText(r.Cell(i))).ToArray())];
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("No se pudo leer el Excel: guárdalo como .xlsx (Libro de Excel).", ex);
        }
    }

    /// <summary>Un teléfono escrito como número en Excel (59170000000) no debe salir como 5,917E+10.</summary>
    private static string CellText(IXLCell cell) =>
        cell.Value.IsNumber ? ((decimal)cell.Value.GetNumber()).ToString("0.##########", CultureInfo.InvariantCulture) : cell.GetString();

    /// <summary>
    /// CSV con ";" (Excel en español), "," o tabulador según el encabezado; comillas dobles para textos con separadores.
    /// UTF-8 (con o sin BOM); si no es UTF-8 válido, Windows-1252/Latin-1 (lo que guarda Excel como "CSV (delimitado)").
    /// </summary>
    private static List<string[]> ReadCsv(Stream content)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n', 2)[0];
        var separator = new[] { ';', ',', '\t' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();

        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }

        return rows;
    }
}
