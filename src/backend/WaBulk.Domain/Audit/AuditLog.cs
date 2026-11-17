namespace WaBulk.Domain.Audit;

/// <summary>
/// Registro de auditoría (tabla AuditLog, ARQUITECTURA): quién hizo qué, sobre qué y cómo estaba antes y después.
/// Se escribe en la MISMA transacción que el cambio: si el cambio se revierte, la auditoría también, y al revés.
/// Antes/Despues son JSON y nunca llevan secretos (claves, tokens).
/// </summary>
public class AuditLog
{
    public long Id { get; set; }

    /// <summary>Quién: usuario de la intranet, o usuario de Windows para los comandos del Worker.</summary>
    public required string User { get; set; }

    /// <summary>Una de <see cref="AuditActions"/>.</summary>
    public required string Action { get; set; }

    /// <summary>Tipo de cosa afectada: "Usuario", "DispatchPause"…</summary>
    public required string Entity { get; set; }

    /// <summary>Cuál: Id del usuario, de la pausa…</summary>
    public string? EntityId { get; set; }

    public string? Before { get; set; }
    public string? After { get; set; }
    public DateTime AtUtc { get; set; }
}

/// <summary>Nombres de acción auditada. Se guardan como texto; F1.4+ suma importaciones, bajas y aprobaciones.</summary>
public static class AuditActions
{
    public const string CrearUsuario = "CrearUsuario";
    public const string CambiarRoles = "CambiarRoles";
    public const string DesactivarUsuario = "DesactivarUsuario";
    public const string ActivarUsuario = "ActivarUsuario";
    public const string RestablecerClave = "RestablecerClave";
    public const string DesbloquearUsuario = "DesbloquearUsuario";
    public const string ReanudarDespacho = "ReanudarDespacho";

    // F1.4: contactos y consentimiento.
    public const string OfrecerPromociones = "OfrecerPromociones";
    public const string BajaManual = "BajaManual";
    public const string ImportarContactos = "ImportarContactos";
    public const string CrearLista = "CrearLista";
    public const string AgregarALista = "AgregarALista";
    public const string QuitarDeLista = "QuitarDeLista";

    // F1.5: plantillas.
    public const string CrearPlantilla = "CrearPlantilla";
    public const string SincronizarPlantillas = "SincronizarPlantillas";

    // F1.6: campañas.
    public const string CrearCampana = "CrearCampana";
    public const string NuevaVersionCampana = "NuevaVersionCampana";
    public const string EnviarAprobacionCampana = "EnviarAprobacionCampana";
    public const string AprobarCampana = "AprobarCampana";
    public const string RechazarCampana = "RechazarCampana";
    public const string PausarCampana = "PausarCampana";
    public const string ReanudarCampana = "ReanudarCampana";
    public const string CancelarCampana = "CancelarCampana";
}
