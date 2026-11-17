namespace WaBulk.Domain.Users;

/// <summary>
/// Roles del sistema (D-005). Un usuario puede tener varios (D-030). Los endpoints no preguntan por estos nombres:
/// usan políticas con nombre (<see cref="Policies"/>), así sumar Microsoft Entra ID después solo cambia cómo se
/// arma cada política (por ejemplo, desde los roles de aplicación de Entra), no los endpoints.
/// </summary>
public static class Roles
{
    public const string Operador = "Operador";
    public const string Aprobador = "Aprobador";
    public const string Admin = "Admin";

    public static readonly IReadOnlyList<string> All = [Operador, Aprobador, Admin];
}

/// <summary>
/// Políticas de autorización con nombre. Todo endpoint de la Api declara una (prueba
/// <c>EndpointPolicyTests</c>); ninguno usa nombres de rol directamente.
/// </summary>
public static class Policies
{
    /// <summary>Cualquier usuario con sesión válida y activo (cerrar sesión, inicio).</summary>
    public const string ConSesion = "ConSesion";

    /// <summary>Preparar contactos, plantillas y campañas: Operador.</summary>
    public const string PuedeEnviar = "PuedeEnviar";

    /// <summary>Aprobar campañas (alguien distinto de quien la creó, F1.6): Aprobador.</summary>
    public const string PuedeAprobar = "PuedeAprobar";

    /// <summary>Usuarios, auditoría y configuración: Admin.</summary>
    public const string PuedeAdministrar = "PuedeAdministrar";

    public static readonly IReadOnlyList<string> All = [ConSesion, PuedeEnviar, PuedeAprobar, PuedeAdministrar];

    /// <summary>Qué rol satisface cada política (hoy 1 a 1). Con Entra ID, este mapa se arma desde sus roles de aplicación.</summary>
    public static readonly IReadOnlyDictionary<string, string> RoleFor = new Dictionary<string, string>
    {
        [PuedeEnviar] = Roles.Operador,
        [PuedeAprobar] = Roles.Aprobador,
        [PuedeAdministrar] = Roles.Admin,
    };
}
