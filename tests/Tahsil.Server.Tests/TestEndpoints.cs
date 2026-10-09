using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;

namespace Tahsil.Server.Tests;

/// <summary>Endpoints for a test, behind Tahsil's own middleware, so errors they raise and filters they use behave as in Tahsil.</summary>
internal sealed class TestEndpoints(Action<IEndpointRouteBuilder> endpoints) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);

        // In a branch, so this routing does not replace the application's when error pages re-execute requests.
        app.UseWhen(_ => true, branch => branch.UseRouting().UseAuthorization().UseEndpoints(endpoints));
    };
}
