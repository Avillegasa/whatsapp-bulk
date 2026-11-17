using Microsoft.AspNetCore.Antiforgery;

namespace WaBulk.Api.Auth;

/// <summary>
/// Valida el token XSRF en todo POST/PUT/PATCH/DELETE del grupo /api. Hace falta explícito: en Minimal APIs la
/// validación automática solo cubre endpoints que leen formularios, no JSON
/// (https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).
/// </summary>
public sealed class XsrfValidationFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsPost(http.Request.Method) || HttpMethods.IsPut(http.Request.Method)
            || HttpMethods.IsPatch(http.Request.Method) || HttpMethods.IsDelete(http.Request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    title: "Token XSRF ausente o inválido",
                    detail: "Recarga la página e intenta de nuevo.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        return await next(context);
    }

    /// <summary>Emite la cookie XSRF-TOKEN (legible por Angular) para el usuario actual de la petición.</summary>
    public static void IssueToken(IAntiforgery antiforgery, HttpContext http)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append(AuthServiceCollectionExtensions.XsrfCookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, // Angular debe poder leerla para copiarla al encabezado
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/",
        });
    }
}
