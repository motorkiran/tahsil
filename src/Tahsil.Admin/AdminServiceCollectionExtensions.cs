using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Tahsil.Admin.Components;
using Tahsil.Admin.Security;

namespace Tahsil.Admin;

public static class AdminServiceCollectionExtensions
{
    /// <summary>
    /// The admin panel: Blazor in the Interactive Server render mode with MudBlazor, behind an admin session. It calls
    /// Application handlers in-process through <see cref="AdminOperations"/>.
    /// </summary>
    public static IServiceCollection AddTahsilAdmin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddMudServices();
        services.AddHttpContextAccessor();

        services.AddCascadingAuthenticationState();
        services.AddScoped<AdminSession>();
        services.AddScoped<AdminSessionLoader>();
        services.AddScoped<AdminOperations>();
        services.AddMemoryCache();
        services.AddScoped<AdminRecheck>();
        services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, AdminRevalidation>();
        services.AddAdminAuthentication(configuration);

        return services;
    }

    /// <summary>Serves the panel under <c>/admin</c>, with its sign-in endpoints.</summary>
    public static IEndpointRouteBuilder MapTahsilAdmin(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAdminAuthentication();
        endpoints.MapRazorComponents<App>()
            // The host's content security policy forbids framing already.
            .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null)
            .RequireAuthorization(AdminAuthentication.Policy);

        return endpoints;
    }

    /// <summary>
    /// The panel loads Blazor's and MudBlazor's scripts and keeps inline styles, which the hosted pages' policy forbids,
    /// so its pages get a policy of their own.
    /// </summary>
    public static IApplicationBuilder UseAdminContentSecurityPolicy(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/admin") || context.Request.Path.StartsWithSegments("/_blazor"))
        {
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; "
                + "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
        }

        return next(context);
    });

    public static bool IsAdminPanelEnabled(this IConfiguration configuration) =>
        configuration.GetValue($"{AdminOptions.SectionName}:{nameof(AdminOptions.Enabled)}", defaultValue: true);
}
