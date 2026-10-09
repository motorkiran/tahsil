using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Tahsil.Server.Ui;

namespace Tahsil.Server.Tests.HostedPages;

[Trait("Requirement", "FND-42")]
public sealed class ContentSecurityPolicyTests
{
    private static readonly CspSources Stripe = new()
    {
        Scripts = ["https://js.stripe.com"],
        Frames = ["https://js.stripe.com", "https://hooks.stripe.com"],
        Connections = ["https://api.stripe.com"],
        FormActions = ["https://hooks.stripe.com"],
    };

    [Fact]
    public void ThePageSources_AreAddedToTheirDirectives_AndNowhereElse()
    {
        var policy = ContentSecurityPolicy.Build("n0nce", runsScripts: false, Stripe);

        policy.ShouldBe(
            "default-src 'self'; script-src 'nonce-n0nce' https://js.stripe.com; style-src 'self' 'nonce-n0nce'; img-src 'self' data: https:; "
            + "frame-src 'self' https://js.stripe.com https://hooks.stripe.com; connect-src 'self' https://api.stripe.com; "
            + "form-action 'self' https://hooks.stripe.com; object-src 'none'; base-uri 'self'; frame-ancestors 'none'");
    }

    [Fact]
    public void PagesWithoutScripts_RunNone()
    {
        ContentSecurityPolicy.Build("n0nce", runsScripts: false).ShouldContain("script-src 'none';");
        ContentSecurityPolicy.Build("n0nce", runsScripts: true).ShouldContain("script-src 'nonce-n0nce';");
    }

    [Fact]
    public async Task TheFilter_TakesTheSourcesOfThePageModel()
    {
        var withSources = await PolicyOfAsync(new ProviderPage());
        var without = await PolicyOfAsync(new PlainPage());

        withSources.ShouldContain("https://js.stripe.com");
        without.ShouldNotContain("stripe");
    }

    private static async Task<string> PolicyOfAsync(PageModel page)
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor { EndpointMetadata = [] });
        var context = new ResultExecutingContext(actionContext, [], new PageResult(), page);

        await new ContentSecurityPolicy.Filter().OnResultExecutionAsync(context, () => Task.FromResult<ResultExecutedContext>(null!));

        return httpContext.Response.Headers.ContentSecurityPolicy.ToString();
    }

    private sealed class ProviderPage : PageModel, IContentSecurityPolicySources
    {
        public CspSources GetCspSources() => Stripe;
    }

    private sealed class PlainPage : PageModel;
}
