using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tahsil.Domain.Common;
using Tahsil.Server.Api;

namespace Tahsil.Server.Tests.ErrorHandling;

public sealed class ErrorHandlingTests
{
    private const string Secret = "connection to provider vault-7 refused";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "FND-23")]
    public async Task ExpectedErrors_AreProblemDetails_WithCodeAndTraceId()
    {
        using var host = Host();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/tests/not-found", UriKind.Relative), CancellationToken);
        var problem = await ReadProblemAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        problem["code"]!.GetValue<string>().ShouldBe("customer.not_found");
        problem["detail"]!.GetValue<string>().ShouldBe("No such customer.");
        problem["traceId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [Trait("Requirement", "FND-22")]
    [InlineData(ErrorType.Validation, HttpStatusCode.BadRequest)]
    [InlineData(ErrorType.Conflict, HttpStatusCode.Conflict)]
    [InlineData(ErrorType.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(ErrorType.PreconditionFailed, HttpStatusCode.PreconditionFailed)]
    [InlineData(ErrorType.RateLimited, HttpStatusCode.TooManyRequests)]
    [InlineData(ErrorType.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public void ErrorTypes_MapToTheirStatus(ErrorType type, HttpStatusCode status)
    {
        ApiResults.StatusCode(type).ShouldBe((int)status);
    }

    [Fact]
    [Trait("Requirement", "FND-24")]
    public async Task FailureErrors_AnswerAGenericServerError_AndAreLoggedWithTheTraceId()
    {
        using var host = Host();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/tests/failure", UriKind.Relative), CancellationToken);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        var problem = JsonNode.Parse(body)!;

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        problem["code"]!.GetValue<string>().ShouldBe("server.error");
        problem["detail"]!.GetValue<string>().ShouldBe("Something went wrong; quote the trace ID.");
        body.ShouldNotContain(Secret);
        body.ShouldNotContain("vault.unreachable");
        var traceId = problem["traceId"]!.GetValue<string>();
        host.Logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Error && entry.Message.Contains(Secret) && entry.Message.Contains(traceId));
    }

    [Fact]
    [Trait("Requirement", "FND-24")]
    public async Task UnhandledExceptions_AnswerAGenericServerError_AndAreLoggedWithTheTraceId()
    {
        using var host = Host();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/tests/throw", UriKind.Relative), CancellationToken);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        var problem = JsonNode.Parse(body)!;

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        problem["code"]!.GetValue<string>().ShouldBe("server.error");
        body.ShouldNotContain(Secret);
        var traceId = problem["traceId"]!.GetValue<string>();
        host.Logs.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException && entry.Message.Contains(traceId));
    }

    [Fact]
    [Trait("Requirement", "FND-14")]
    public async Task LostConcurrencyRaces_AreConflicts()
    {
        using var host = Host();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/tests/concurrency", UriKind.Relative), CancellationToken);
        var problem = await ReadProblemAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        problem["code"]!.GetValue<string>().ShouldBe("concurrency.conflict");
        host.Logs.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    [Trait("Requirement", "FND-23")]
    public async Task MalformedParameters_AndBodies_AreInvalidRequests()
    {
        using var host = Host();
        using var client = host.CreateClient();

        using var parameter = await client.GetAsync(new Uri("/api/v1/tests/items/not-a-number", UriKind.Relative), CancellationToken);
        using var body = await client.PostAsync(
            new Uri("/api/v1/tests/items", UriKind.Relative),
            new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"),
            CancellationToken);

        parameter.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(parameter))["code"]!.GetValue<string>().ShouldBe("request.invalid");
        body.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(body))["code"]!.GetValue<string>().ShouldBe("request.invalid");
    }

    private static TestHost Host() => new(endpoints: endpoints =>
    {
        endpoints.MapGet("/tests/not-found", () => ApiResults.Problem(Error.NotFound("customer.not_found", "No such customer.")));
        endpoints.MapGet("/tests/failure", () => ApiResults.Problem(Error.Failure("vault.unreachable", Secret)));
        endpoints.MapGet("/tests/throw", IResult () => throw new InvalidOperationException(Secret));
        endpoints.MapGet("/tests/concurrency", IResult () => throw new DbUpdateConcurrencyException("Lost the race."));

        var api = endpoints.MapGroup("/api/v1/tests").AddEndpointFilter<ParameterBindingFilter>();
        api.MapGet("/items/{id:int}", (int id) => TypedResults.Ok(id));
        api.MapGet("/items/{id}", (int id) => TypedResults.Ok(id));
        api.MapPost("/items", (TestItem item) => TypedResults.Ok(item));
    });

    private static async Task<JsonNode> ReadProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>(CancellationToken))!;

    public sealed record TestItem(string Name);
}
