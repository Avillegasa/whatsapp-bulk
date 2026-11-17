namespace WaBulk.Domain.Users;

/// <summary>
/// Roles del sistema (D-005). Los endpoints no preguntan por estos nombres: usan políticas con nombre
/// (<see cref="Policies"/>), así sumar Microsoft Entra ID después solo cambia cómo se arma la política.
/// </summary>
public static class Roles
{
    public const string Operador = "Operador";
    public const string Aprobador = "Aprobador";
    public const string Admin = "Admin";

    public static readonly IReadOnlyList<string> All = [Operador, Aprobador, Admin];
}

/// <summary>Políticas de autorización con nombre. F1.3 suma las demás (PuedeAprobar, PuedeEnviar…).</summary>
public static class Policies
{
    /// <summary>Administrar usuarios y configuración: solo Admin.</summary>
    public const string PuedeAdministrar = "PuedeAdministrar";
}
