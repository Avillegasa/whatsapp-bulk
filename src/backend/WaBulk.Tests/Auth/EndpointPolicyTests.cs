using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WaBulk.Api;
using WaBulk.Domain.Users;

namespace WaBulk.Tests.Auth;

/// <summary>
/// F1.3: cada endpoint de la Api declara una política con nombre de <see cref="Policies"/> y ninguno usa nombres de
/// rol. Las únicas excepciones son las anónimas a propósito (lista cerrada). Un endpoint nuevo sin política hace
/// fallar esta prueba, aunque la política por defecto ya exija sesión.
/// </summary>
public sealed class EndpointPolicyTests
{
    private static readonly string[] AnonymousOnPurpose = ["/health", "/api/auth/sesion", "/api/auth/login"];

    [Fact]
    public async Task CadaEndpoint_TieneUnaPoliticaConNombre_OEsAnonimoAPropósito()
    {
        await using var factory = new WebApplicationFactory<ApiAssemblyMarker>();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var problems = new List<string>();
        foreach (var e in endpoints)
        {
            var route = "/" + e.RoutePattern.RawText!.TrimStart('/');
            var name = $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {route}";
            var authorize = e.Metadata.GetOrderedMetadata<IAuthorizeData>();
            var anonymous = e.Metadata.GetMetadata<IAllowAnonymous>() is not null;

            if (AnonymousOnPurpose.Contains(route))
            {
                if (!anonymous)
                {
                    problems.Add($"{name}: debería ser anónimo");
                }

                continue;
            }

            if (anonymous)
            {
                problems.Add($"{name}: anónimo sin estar en la lista");
            }

            if (!authorize.Any(a => a.Policy is not null && Policies.All.Contains(a.Policy)))
            {
                problems.Add($"{name}: sin política con nombre");
            }

            if (authorize.Any(a => !string.IsNullOrEmpty(a.Roles)))
            {
                problems.Add($"{name}: usa nombres de rol en vez de una política");
            }
        }

        Assert.Empty(problems);
        // Que la prueba de verdad vea los endpoints (si cambia el ruteo y la lista sale vacía, no pasa en blanco).
        Assert.True(endpoints.Count >= 10, $"Solo se encontraron {endpoints.Count} endpoints");
        Assert.All(AnonymousOnPurpose, path => Assert.Contains(endpoints, e => "/" + e.RoutePattern.RawText!.TrimStart('/') == path));
    }
}
