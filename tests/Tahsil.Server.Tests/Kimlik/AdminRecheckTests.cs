using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Admin.Security;
using Tahsil.Application.Kimlik;

namespace Tahsil.Server.Tests.Kimlik;

/// <summary>Panel sessions signed in through Kimlik, rechecked through Kimlik's Management API (KML-9), with <see cref="KimlikStub"/>.</summary>
public sealed class AdminRecheckTests : IDisposable
{
    private readonly TahsilServerFixture _server;
    private readonly FakeTimeProvider _time;
    private readonly KimlikStub _kimlik;

    public AdminRecheckTests(TahsilServerFixture server)
    {
        _server = server;
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        _kimlik = new KimlikStub(_time);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _kimlik.Dispose();

    [Fact]
    [Trait("Requirement", "KML-9")]
    public async Task ASession_EndsWhenItsUserIsGoneSuspendedOrHoldsAnotherRole_AndStaysWhileKimlikIsDown()
    {
        await using var host = Host();
        await using var scope = host.Services.CreateAsyncScope();
        var recheck = scope.ServiceProvider.GetRequiredService<AdminRecheck>();
        var admin = _kimlik.AddUser(KimlikUserStatus.Active, "tahsil-admin", "billing-team");
        var suspended = _kimlik.AddUser(KimlikUserStatus.Suspended, "tahsil-admin");
        var viewer = _kimlik.AddUser(KimlikUserStatus.Active, "tahsil-viewer");
        var roleless = _kimlik.AddUser(KimlikUserStatus.Active, "billing-team");

        // Each a session of its own, which no earlier check covers.
        var started = DateTimeOffset.UtcNow;
        Task<bool> GoesOnAsync(Guid user, string role = AdminRoles.Admin) =>
            recheck.SessionGoesOnAsync(Session(user, role), started = started.AddSeconds(1), CancellationToken);

        (await GoesOnAsync(admin)).ShouldBeTrue();
        (await GoesOnAsync(suspended)).ShouldBeFalse();
        (await GoesOnAsync(Guid.CreateVersion7())).ShouldBeFalse("the user was deleted");
        (await GoesOnAsync(roleless)).ShouldBeFalse();
        (await GoesOnAsync(viewer)).ShouldBeFalse("the role changed; the next sign-in takes it");
        (await GoesOnAsync(viewer, AdminRoles.Viewer)).ShouldBeTrue();
        _kimlik.Down = true;
        (await GoesOnAsync(suspended)).ShouldBeTrue("Kimlik could not tell");
        _kimlik.Down = false;
        var requests = _kimlik.Requests.Count;
        recheck.Applies(AdminClaims.Principal("development", null, "Developer", AdminRoles.Admin, AdminAuthentication.CookieScheme)).ShouldBeFalse();
        _kimlik.Requests.Count.ShouldBe(requests);
    }

    [Fact]
    [Trait("Requirement", "KML-9")]
    public async Task TheCookie_IsCheckedEveryFiveMinutes_AndRejectedOnceTheSessionIsOver()
    {
        await using var host = Host();
        await using var scope = host.Services.CreateAsyncScope();
        var user = _kimlik.AddUser(KimlikUserStatus.Active, "tahsil-operator");
        var issued = DateTimeOffset.UtcNow;

        var first = await ValidateAsync(scope.ServiceProvider, Session(user, AdminRoles.Operator), issued);
        _kimlik.Users[user] = _kimlik.Users[user] with { Status = KimlikUserStatus.Suspended };
        var cached = await ValidateAsync(scope.ServiceProvider, Session(user, AdminRoles.Operator), issued);
        var later = await ValidateAsync(scope.ServiceProvider, Session(user, AdminRoles.Operator), issued.AddSeconds(1));

        first.Principal.ShouldNotBeNull();
        cached.Principal.ShouldNotBeNull("checked less than five minutes ago");
        later.Principal.ShouldBeNull();
        later.ShouldRenew.ShouldBeFalse("rewriting the cookie would extend the session's absolute lifetime");
        _kimlik.Requests.Count(request => request.Path == $"/api/v1/users/{user}").ShouldBe(2);
    }

    private static ClaimsPrincipal Session(Guid user, string role) =>
        AdminClaims.Principal(user.ToString(), $"{user:N}@example.com", "Ada", role, AdminAuthentication.OidcScheme);

    private static async Task<CookieValidatePrincipalContext> ValidateAsync(IServiceProvider services, ClaimsPrincipal principal, DateTimeOffset issued)
    {
        var scheme = new AuthenticationScheme(AdminAuthentication.CookieScheme, null, typeof(CookieAuthenticationHandler));
        var properties = new AuthenticationProperties { IssuedUtc = issued, ExpiresUtc = issued.AddHours(8) };
        var context = new CookieValidatePrincipalContext(
            new DefaultHttpContext { RequestServices = services }, scheme, new CookieAuthenticationOptions(), new AuthenticationTicket(principal, properties, scheme.Name));
        await AdminRecheck.ValidateAsync(context);
        return context;
    }

    private WebApplicationFactory<Program> Host()
    {
        var settings = KimlikStub.Settings;
        settings["Tahsil:Admin:Oidc:Authority"] = KimlikStub.Authority.AbsoluteUri;
        settings["Tahsil:Admin:Oidc:ClientId"] = "tahsil-admin";
        settings["Tahsil:Admin:Oidc:ClientSecret"] = "admin-secret";
        settings["Tahsil:Admin:Oidc:RoleSource"] = "AccessToken";
        settings["Tahsil:Admin:Oidc:AccessTokenAudience"] = "tahsil";
        settings["Tahsil:Admin:Oidc:RoleMappings:tahsil-admin"] = AdminRoles.Admin;
        settings["Tahsil:Admin:Oidc:RoleMappings:tahsil-operator"] = AdminRoles.Operator;
        settings["Tahsil:Admin:Oidc:RoleMappings:tahsil-viewer"] = AdminRoles.Viewer;
        return IsolatedHosts.Create(_server, _server.NewDatabase("admin_recheck"), _time, _kimlik.Register, settings);
    }
}
