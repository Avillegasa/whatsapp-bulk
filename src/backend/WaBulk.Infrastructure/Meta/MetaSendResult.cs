using WaBulk.Domain.Messaging;

namespace WaBulk.Infrastructure.Meta;

/// <summary>Resultado clasificado de un POST a /messages.</summary>
/// <param name="Status">Accepted, Rejected o SubmissionUnknown (nunca Submitting).</param>
/// <param name="Wamid">Id del mensaje si Meta lo aceptó.</param>
/// <param name="HttpStatus">Código HTTP si hubo respuesta.</param>
/// <param name="ErrorCode">error.code de Meta si lo informó.</param>
/// <param name="Detail">Texto para diagnóstico (mensaje de error, message_status o excepción).</param>
public sealed record MetaSendResult(
    SendAttemptStatus Status,
    string? Wamid,
    int? HttpStatus,
    int? ErrorCode,
    string? Detail);
