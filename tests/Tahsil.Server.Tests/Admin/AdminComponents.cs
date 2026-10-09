using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor;
using MudBlazor.Services;
using Tahsil.Admin.Security;

namespace Tahsil.Server.Tests.Admin;

/// <summary>
/// Renders admin panel components with bUnit, as a signed-in administrator, against the real Tahsil under test: the
/// components' operations run in scopes of the test server, so they read and change its database.
/// </summary>
internal sealed class AdminComponents : BunitContext
{
    // The components wait on the database of the test server, which a parallel test run keeps busy.
    static AdminComponents() => DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    /// <param name="server">The Tahsil under test.</param>
    /// <param name="role">The administrator's panel role.</param>
    /// <param name="withProviders">
    /// Renders MudBlazor's providers for dialogs and notifications; tests that render the router leave them to the layout.
    /// </param>
    public AdminComponents(WebApplicationFactory<Program> server, string role = AdminRoles.Admin, bool withProviders = true)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        Subject = $"tests-{Guid.CreateVersion7():N}";
        Principal = AdminClaims.Principal(Subject, $"{role}@example.com", "Test Admin", role, AdminAuthentication.OidcScheme);
        var session = new AdminSession();
        session.Load(Principal, null, null);
        Services.AddSingleton(session);
        Services.AddSingleton(new AdminOperations(server.Services.GetRequiredService<IServiceScopeFactory>(), session));

        // What the layout needs, for pages rendered through the router.
        Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(new Microsoft.AspNetCore.Http.HttpContextAccessor());
        Services.AddSingleton<AdminSessionLoader>();
        Services.AddSingleton(server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Tahsil.Application.Common.ServerOptions>>());

        // The panel's own policies, evaluated for real against the administrator's claims, instead of bUnit's stand-ins.
        Services.RemoveAll<IAuthorizationService>();
        Services.RemoveAll<AuthenticationStateProvider>();
        Services.AddAuthorizationCore(AdminAuthentication.AddPolicies);
        Services.AddSingleton<AuthenticationStateProvider>(new FixedAuthenticationState(Principal));
        Services.AddCascadingAuthenticationState();

        // Dialogs, message boxes, select menus and notifications render into these providers.
        if (withProviders)
        {
            Render<MudPopoverProvider>();
            Dialogs = Render<MudDialogProvider>();
            Notifications = Render<MudSnackbarProvider>();
        }
    }

    /// <summary>The administrator's subject, the actor of what they do.</summary>
    public string Subject { get; }

    public ClaimsPrincipal Principal { get; }

    public IRenderedComponent<MudDialogProvider> Dialogs { get; } = null!;

    public IRenderedComponent<MudSnackbarProvider> Notifications { get; } = null!;

    /// <summary>Waits until the administrator is told <paramref name="message"/>.</summary>
    public void WaitForNotification(string message) =>
        Notifications.WaitForAssertion(() => Notifications.Markup.ShouldContain(message));

    /// <summary>Clicks the button in the open dialog whose text is <paramref name="text"/>.</summary>
    public void Confirm(string text)
    {
        Dialogs.WaitForAssertion(() => Dialogs.FindAll("button").ShouldContain(button => button.TextContent.Trim() == text));

        // Found and clicked in one go, so that no render in between leaves the click on a stale button.
        Dialogs.InvokeAsync(() => Dialogs.FindAll("button").Single(button => button.TextContent.Trim() == text).Click());
    }

    /// <summary>Types into the open dialog's field labelled <paramref name="label"/>.</summary>
    public void Fill(string label, string value)
    {
        Dialogs.WaitForAssertion(() => FieldInput(label).ShouldNotBeNull());
        Dialogs.InvokeAsync(() => FieldInput(label)!.Change(value));
    }

    private AngleSharp.Dom.IElement? FieldInput(string label) =>
        Dialogs.FindAll(".mud-input-control")
            .FirstOrDefault(control => control.QuerySelector("label")?.TextContent.Trim() == label)
            ?.QuerySelector("input, textarea");

    private sealed class FixedAuthenticationState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
