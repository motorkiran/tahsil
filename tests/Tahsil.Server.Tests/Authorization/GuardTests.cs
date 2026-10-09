using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Common;
using Tahsil.Server.Api;

namespace Tahsil.Server.Tests.Authorization;

/// <summary>Proves that no endpoint or handler forgot its authorization (both checks are empty until M1 adds use cases).</summary>
[Trait("Requirement", "FND-27")]
public sealed class GuardTests
{
    [Fact]
    public void EveryApiEndpoint_DeclaresAPermission()
    {
        using var host = new TestHost();
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        var undeclared = endpoints
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(TahsilApi.BasePath[1..], StringComparison.Ordinal) == true
                || endpoint.RoutePattern.RawText?.StartsWith(TahsilApi.BasePath, StringComparison.Ordinal) == true)
            .Where(endpoint => endpoint.Metadata.GetMetadata<RequiredPermission>() is null)
            .Select(endpoint => endpoint.DisplayName);

        undeclared.ShouldBeEmpty();
    }

    [Fact]
    public void EveryHandler_TakesTheAuthorizer()
    {
        var handlers = typeof(IAuthorizer).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.Name.EndsWith("Handler", StringComparison.Ordinal));

        var unguarded = handlers
            .Where(type => !type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IAuthorizer))))
            .Select(type => type.FullName);

        unguarded.ShouldBeEmpty();
    }
}
