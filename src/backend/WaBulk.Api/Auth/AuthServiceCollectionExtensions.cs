using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WaBulk.Domain.Users;
using WaBulk.Infrastructure.Identity;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Api.Auth;

/// <summary>
/// Identity con cookie (D-005) + antiforgery para la SPA de Angular.
/// - Identity sin UI: https://learn.microsoft.com/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0
/// - XSRF con SPA (cookie XSRF-TOKEN legible por JS + encabezado X-XSRF-TOKEN):
///   https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0 (sección JavaScript/SPA)
/// - Angular lee XSRF-TOKEN y envía X-XSRF-TOKEN en POST/PUT/PATCH/DELETE a URLs relativas:
///   https://angular.dev/best-practices/security#httpclient-xsrf-csrf-security
/// </summary>
public static class AuthServiceCollectionExtensions
{
    public const string XsrfCookieName = "XSRF-TOKEN";
    public const string XsrfHeaderName = "X-XSRF-TOKEN";
    public const string AuthCookieName = "WaBulk.Auth";
    public const int MaxFailedAccessAttempts = 5;

    public static IServiceCollection AddWaBulkAuth(this IServiceCollection services, IHostEnvironment environment)
    {
        // En desarrollo la Api corre en http://localhost (proxy de ng serve); en Docker/producción siempre HTTPS.
        var securePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();

        services.AddIdentityCore<AppUser>(options =>
            {
                options.Password.RequiredLength = 10;
                options.User.RequireUniqueEmail = false;
                // Bloqueo: 5 claves malas seguidas → 15 minutos sin poder entrar (también para usuarios nuevos).
                options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<WaBulkDbContext>()
            .AddSignInManager<AppSignInManager>()
            .AddDefaultTokenProviders();

        // La cookie se revalida contra la base en CADA petición (intervalo 0): usuario desactivado o con clave
        // restablecida → fuera en la siguiente petición; cambio de roles → la sesión los toma sin volver a entrar.
        // Cuesta unas pocas consultas por petición, nada para una intranet de pocos usuarios.
        // https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.identity.securitystampvalidatoroptions.validationinterval
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = AuthCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = securePolicy;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            // Es una API: sin redirección a una página de login, responde 401/403 y Angular decide.
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = XsrfHeaderName;
            options.Cookie.Name = "WaBulk.Antiforgery";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = securePolicy;
        });

        // Políticas con nombre (D-030): los endpoints solo conocen estos nombres. Con Entra ID, cada política se
        // arma desde sus roles de aplicación y los endpoints no cambian.
        var authorization = services.AddAuthorizationBuilder()
            // Red de seguridad: un endpoint sin política exige igual sesión (la prueba EndpointPolicyTests lo prohíbe).
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.ConSesion, p => p.RequireAuthenticatedUser());
        foreach (var (policy, role) in Policies.RoleFor)
        {
            authorization.AddPolicy(policy, p => p.RequireAuthenticatedUser().RequireRole(role));
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AdminBootstrapper>();
        services.AddScoped<UserAdminService>();
        return services;
    }
}
