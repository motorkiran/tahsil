namespace Tahsil.Server.Hosting;

/// <summary>
/// Endpoints that services contribute to the application, mapped with its own (such as the test endpoints of the
/// integration tests), so they share its routing, middleware and error handling.
/// </summary>
internal interface IEndpointMapper
{
    void Map(IEndpointRouteBuilder endpoints);
}

internal static class EndpointMappers
{
    public static IEndpointRouteBuilder MapContributedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        foreach (var mapper in endpoints.ServiceProvider.GetServices<IEndpointMapper>())
        {
            mapper.Map(endpoints);
        }

        return endpoints;
    }
}
