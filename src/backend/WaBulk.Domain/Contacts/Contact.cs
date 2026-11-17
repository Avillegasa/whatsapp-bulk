namespace WaBulk.Domain.Contacts;

/// <summary>
/// Un cliente al que se le puede escribir (tabla Contact, ARQUITECTURA). Se identifica por teléfono (E.164) o por su
/// BSUID (user_id de Meta, "BO.1880967352889250"): Meta omite el teléfono cuando el usuario adopta un nombre de
/// usuario, así que un contacto puede llegar solo con BSUID y completarse cuando aparezca el teléfono (D-032).
/// </summary>
public class Contact
{
    public long Id { get; set; }

    /// <summary>"+59170000000". Único cuando existe.</summary>
    public string? PhoneE164 { get; set; }

    /// <summary>wa_id de Meta ("59170000000"); igual al teléfono sin "+".</summary>
    public string? WaId { get; set; }

    /// <summary>BSUID (business-scoped user id) de Meta. Único cuando existe.</summary>
    public string? UserId { get; set; }

    /// <summary>Nombre: el de la importación o, si no hay, el del perfil de WhatsApp.</summary>
    public string? Name { get; set; }

    /// <summary>Código del cliente en el sistema comercial de la empresa.</summary>
    public string? CustomerId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }

    /// <summary>"webhook", "importacion:&lt;usuario&gt;", usuario de la intranet…</summary>
    public required string CreatedBy { get; set; }
}

/// <summary>Mensaje que un cliente escribió al número (tabla InboundMessage). Sirve para saber si la ventana de 24 h está abierta.</summary>
public class InboundMessage
{
    public long Id { get; set; }
    public required string Wamid { get; set; }
    public long ContactId { get; set; }
    public string? PhoneE164 { get; set; }
    public string? UserId { get; set; }

    /// <summary>text, button, interactive, image…</summary>
    public required string Type { get; set; }

    /// <summary>Cuerpo del texto o título del botón tocado.</summary>
    public string? Text { get; set; }

    /// <summary>Payload o id del botón tocado.</summary>
    public string? ButtonId { get; set; }

    /// <summary>wamid del mensaje nuestro al que responde (context.id).</summary>
    public string? ContextWamid { get; set; }

    /// <summary>Hora del mensaje según Meta (timestamp).</summary>
    public DateTime SentUtc { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public long WebhookInboxId { get; set; }
}

/// <summary>Resultado del POST de una oferta. Igual que SendAttempt: se guarda antes del POST y nunca se reenvía sola.</summary>
public enum ConsentOfferStatus
{
    /// <summary>Guardada; el POST está en curso (o el proceso cayó durante el POST).</summary>
    Enviando,
    Enviada,
    Rechazada,
    /// <summary>Sin respuesta confiable de Meta (regla 4): no se reenvía.</summary>
    Incierta,
}

public enum ConsentOfferResponse
{
    Si,
    AhoraNo,
}

/// <summary>
/// "¿Quieres recibir nuestras promociones?" enviada por un encargado dentro de la ventana de 24 h (D-031). Guarda la
/// versión del texto y quién la ofreció: son la evidencia del OptIn cuando el cliente toca «Sí».
/// </summary>
public class ConsentOffer
{
    public long Id { get; set; }
    public long ContactId { get; set; }
    public string? PhoneE164 { get; set; }
    public required string TextVersion { get; set; }
    public required string Text { get; set; }
    public required string OfferedBy { get; set; }
    public DateTime OfferedUtc { get; set; }
    public ConsentOfferStatus Status { get; set; } = ConsentOfferStatus.Enviando;
    public string? Wamid { get; set; }
    public int? HttpStatus { get; set; }
    public int? ErrorCode { get; set; }
    public string? Detail { get; set; }
    public ConsentOfferResponse? Response { get; set; }
    public DateTime? ResponseUtc { get; set; }
    public string? ResponseWamid { get; set; }
}

/// <summary>Lista estática de contactos (tabla Segment). Se congela al aprobar una campaña (F1.6).</summary>
public class Segment
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedUtc { get; set; }
    public required string CreatedBy { get; set; }
}

public class SegmentMember
{
    public long SegmentId { get; set; }
    public long ContactId { get; set; }
    public DateTime AddedUtc { get; set; }
    public required string AddedBy { get; set; }
}
