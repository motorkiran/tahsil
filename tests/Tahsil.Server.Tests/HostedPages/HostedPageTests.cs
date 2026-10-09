using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Tahsil.Server.Tests.HostedPages;

public sealed class HostedPageTests
{
    private const string BasePolicy =
        "default-src 'self'; script-src 'none'; style-src 'self' 'nonce-{nonce}'; img-src 'self' data: https:; frame-src 'self'; "
        + "connect-src 'self'; form-action 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Requirement", "FND-41")]
    [Trait("Requirement", "FND-42")]
    [InlineData("tr", "Sayfa bulunamadı", "Test modu: gerçek ödeme alınmaz.")]
    [InlineData("en", "Page not found", "Test mode: no real payments are made.")]
    public async Task Pages_RenderInTheirLanguage_WithTheBasePolicy(string locale, string title, string banner)
    {
        using var host = new TestHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri($"/error?status=404&locale={locale}", UriKind.Relative), CancellationToken);
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(CancellationToken));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        document.DocumentElement.GetAttribute("lang").ShouldBe(locale);
        document.QuerySelector("h1")!.TextContent.ShouldBe(title);
        document.QuerySelector(".test-banner")!.TextContent.ShouldBe(banner);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = document.QuerySelector("style")!.GetAttribute("nonce")!;
        policy.ShouldBe(BasePolicy.Replace("{nonce}", nonce, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "FND-10")]
    public async Task LiveMode_ShowsNoTestBanner()
    {
        using var host = new TestHost(new Dictionary<string, string?>
        {
            ["Tahsil:Server:Mode"] = "Live",
            ["Tahsil:Server:PublicUrl"] = "https://billing.example.com/",
            ["Tahsil:Server:RequireHttps"] = "true",
        });
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/error?status=404", UriKind.Relative), CancellationToken);

        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldNotContain("test-banner");
    }

    [Fact]
    [Trait("Requirement", "FND-41")]
    public async Task EveryResponse_CarriesTheSecurityHeaders()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/error", UriKind.Relative), CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
    }

    [Fact]
    [Trait("Requirement", "FND-25")]
    public async Task MissingPages_ShowTheLocalizedErrorPage_WhileTheApiKeepsProblemDetails()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();

        using var page = await client.GetAsync(new Uri("/checkout/unknown-token?locale=tr", UriKind.Relative), CancellationToken);
        using var api = await client.GetAsync(new Uri("/api/v1/unknown", UriKind.Relative), CancellationToken);

        page.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        page.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        (await page.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("Sayfa bulunamadı");
        api.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        api.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    [Trait("Requirement", "FND-25")]
    public async Task FailingPages_ShowTheTraceId_AndOfferToTryAgain()
    {
        using var host = new TestHost(endpoints: endpoints =>
            endpoints.MapGet("/portal/failing", IResult () => throw new InvalidOperationException("Broken page.")));
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/portal/failing?locale=en", UriKind.Relative), CancellationToken);
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(CancellationToken));

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        document.QuerySelector("h1")!.TextContent.ShouldBe("Something went wrong");
        document.QuerySelector(".actions a")!.GetAttribute("href").ShouldBe("/portal/failing?locale=en");
        var traceId = document.QuerySelector(".trace span")!.TextContent;
        traceId.ShouldNotBeNullOrWhiteSpace();
        host.Logs.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException && entry.Message.Contains(traceId));
    }

    [Theory]
    [Trait("Requirement", "FND-41")]
    [InlineData("/portal/abc", "/portal/abc")]
    [InlineData("//evil.example.com/", "/")]
    [InlineData("https://evil.example.com/", "/")]
    public async Task TheLanguageSwitch_RemembersTheChoice_AndOnlyReturnsToThisSite(string returnUrl, string expected)
    {
        using var host = new TestHost();
        using var client = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri($"/culture/en?returnUrl={Uri.EscapeDataString(returnUrl)}", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe(expected);
        response.Headers.GetValues("Set-Cookie").ShouldContain(cookie => cookie.StartsWith(".AspNetCore.Culture=c%3Den%7Cuic%3Den", StringComparison.Ordinal));
    }
}
