using Microsoft.AspNetCore.Routing;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Tests;

/// <summary>Endpoints for a test, mapped with Tahsil's own, so its routing, filters and error handling apply to them.</summary>
internal sealed class TestEndpoints(Action<IEndpointRouteBuilder> endpoints) : IEndpointMapper
{
    public void Map(IEndpointRouteBuilder routes) => endpoints(routes);
}
