using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Tahsil.Admin.Security;

namespace Tahsil.Server.Tests.Admin;

public sealed class AdminAccessTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "ADM-1")]
    public async Task Strangers_AreSentToSignIn()
    {
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri("/admin", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.PathAndQuery.ShouldBe("/admin/signin?returnUrl=%2Fadmin");
    }

    [Theory]
    [Trait("Requirement", "KML-2")]
    [InlineData(AdminRoles.Admin, HttpStatusCode.OK, null)]
    [InlineData(AdminRoles.Viewer, HttpStatusCode.OK, null)]
    [InlineData("auditor", HttpStatusCode.Redirect, "/admin/denied")]
    public async Task OnlySessionsWithAPanelRole_OpenThePanel(string role, HttpStatusCode status, string? location)
    {
        await using var host = HostWithTestSignIn();
        using var client = await SignedInClientAsync(host, role);

        using var response = await client.GetAsync(new Uri("/admin", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(status);
        response.Headers.Location?.AbsolutePath.ShouldBe(location);
    }

    [Theory]
    [Trait("Requirement", "KML-6")]
    [InlineData("Testing", "Test")]
    [InlineData("Development", "Live")]
    public void TheDevelopmentSignIn_IsRefusedOutsideTestModeDevelopment(string environment, string mode)
    {
        var settings = new Dictionary<string, string?> { ["Tahsil:Admin:DevelopmentSignIn"] = "true", ["Tahsil:Server:Mode"] = mode };
        if (mode == "Live")
        {
            settings["Tahsil:Server:PublicUrl"] = "https://billing.example.com/";
            settings["Tahsil:Server:RequireHttps"] = "true";
        }

        using var host = server.WithSettings(settings, environment: environment);

        var failure = Should.Throw<Exception>(host.CreateClient);
        var messages = failure is AggregateException aggregate ? aggregate.InnerExceptions : [failure];
        messages.ShouldContain(exception => exception is OptionsValidationException && exception.Message.Contains("DevelopmentSignIn"));
    }

    [Fact]
    [Trait("Requirement", "KML-6")]
    public async Task TheDeveloper_SignsInWithTheAdminRole_InDevelopment()
    {
        await using var host = server.WithSettings(new Dictionary<string, string?> { ["Tahsil:Admin:DevelopmentSignIn"] = "true" }, environment: "Development");
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var page = await new HtmlParser().ParseDocumentAsync(await client.GetStringAsync(new Uri("/admin/signin?returnUrl=%2Fadmin&locale=en", UriKind.Relative), CancellationToken));
        var form = page.QuerySelector("form")!;
        var fields = form.QuerySelectorAll("input[type=hidden]").ToDictionary(input => input.GetAttribute("name")!, input => input.GetAttribute("value") ?? string.Empty);
        using var signIn = await client.PostAsync(new Uri(form.GetAttribute("action")!, UriKind.Relative), new FormUrlEncodedContent(fields), CancellationToken);
        using var panel = await client.GetAsync(new Uri("/admin", UriKind.Relative), CancellationToken);

        page.QuerySelector("#sign-in-provider").ShouldBeNull();
        signIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        signIn.Headers.Location!.OriginalString.ShouldBe("/admin");
        panel.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Requirement", "KML-6")]
    public async Task WithoutTheSetting_TheSignInPageOffersNoDeveloperSession()
    {
        using var client = server.CreateClient();

        var page = await new HtmlParser().ParseDocumentAsync(await client.GetStringAsync(new Uri("/admin/signin?locale=en", UriKind.Relative), CancellationToken));

        page.QuerySelector("#sign-in-developer").ShouldBeNull();
        page.QuerySelector(".lead")!.TextContent.ShouldContain("Tahsil:Admin:Oidc");
    }

    /// <summary>A host with an endpoint that starts an admin session with any role, standing in for the provider.</summary>
    internal static WebApplicationFactory<Program> HostWithTestSignIn(TahsilServerFixture server, string? database = null) => server.WithSettings(
        new Dictionary<string, string?> { ["ConnectionStrings:Tahsil"] = database ?? server.NewDatabase("admin") },
        endpoints: endpoints => endpoints.MapGet("/tests/admin-signin", async (string role, HttpContext context) =>
        {
            await context.SignInAsync(
                AdminAuthentication.CookieScheme,
                AdminClaims.Principal($"tests-{role}", $"{role}@example.com", role, role, AdminAuthentication.OidcScheme));
            return Results.Ok();
        }));

    internal static async Task<HttpClient> SignedInClientAsync(WebApplicationFactory<Program> host, string role)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        (await client.GetAsync(new Uri($"/tests/admin-signin?role={role}", UriKind.Relative), CancellationToken)).EnsureSuccessStatusCode();
        return client;
    }

    private WebApplicationFactory<Program> HostWithTestSignIn() => HostWithTestSignIn(server);
}
