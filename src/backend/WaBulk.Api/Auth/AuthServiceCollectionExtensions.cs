using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
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
            .AddSignInManager()
            .AddDefaultTokenProviders();

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

        services.AddAuthorizationBuilder()
            // Todo endpoint exige sesión salvo que diga AllowAnonymous (login, sesión, /health).
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.PuedeAdministrar, p => p.RequireRole(Roles.Admin));

        services.AddScoped<AdminBootstrapper>();
        return services;
    }
}
