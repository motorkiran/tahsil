using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Server.Api;

namespace Tahsil.Server.Tests.Idempotency;

public sealed class IdempotencyTests(TahsilServerFixture server)
{
    private const string WidgetType = "widget";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "API-12")]
    public async Task ARepeatedRequest_GetsTheKeptResponse_WithoutActingTwice()
    {
        await using var host = Host(new Widgets());
        using var client = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite);
        var request = new CreateApiKeyRequest { Name = "App", Permissions = [TahsilPermissions.ApiKeysWrite] };

        using var first = await SendAsync(client, "/api/v1/api-keys", request, "key-1");
        using var second = await SendAsync(client, "/api/v1/api-keys", request, "key-1");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        first.Headers.Contains(IdempotencyFilter.ReplayedHeader).ShouldBeFalse();
        second.Headers.GetValues(IdempotencyFilter.ReplayedHeader).ShouldBe(["true"]);
        second.Headers.Location.ShouldBe(first.Headers.Location);
        second.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        (await second.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(await first.Content.ReadAsStringAsync(CancellationToken));
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.api_keys")).ShouldBe([2]);
    }

    [Fact]
    [Trait("Requirement", "API-12")]
    public async Task AKeyReusedForAnotherRequest_IsRefused()
    {
        await using var host = Host(new Widgets());
        using var client = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite);

        using var first = await SendAsync(client, "/api/v1/api-keys", new CreateApiKeyRequest { Name = "One", Permissions = [TahsilPermissions.ApiKeysWrite] }, "key-1");
        using var other = await SendAsync(client, "/api/v1/api-keys", new CreateApiKeyRequest { Name = "Two", Permissions = [TahsilPermissions.ApiKeysWrite] }, "key-1");

        other.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await other.ProblemCodeAsync()).ShouldBe("idempotency.key_reused");
    }

    [Fact]
    [Trait("Requirement", "API-11")]
    public async Task KeysBelongToTheApiKeyThatSendsThem()
    {
        await using var host = Host(new Widgets());
        using var one = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite);
        using var two = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite);
        var request = new CreateApiKeyRequest { Name = "App", Permissions = [TahsilPermissions.ApiKeysWrite] };

        using var first = await SendAsync(one, "/api/v1/api-keys", request, "shared");
        using var second = await SendAsync(two, "/api/v1/api-keys", request, "shared");

        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.Contains(IdempotencyFilter.ReplayedHeader).ShouldBeFalse();
    }

    [Theory]
    [Trait("Requirement", "API-11")]
    [InlineData("tab\tkey")]
    [InlineData("ş")]
    public async Task MalformedKeys_AreInvalidRequests(string key)
    {
        await using var host = Host(new Widgets());
        using var client = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite);

        using var tooLong = await SendAsync(client, "/api/v1/tests/widgets", new { }, new string('k', 256));
        using var response = await SendRawAsync(client, "/api/v1/tests/widgets", key);

        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ProblemCodeAsync()).ShouldBe("request.invalid");
    }

    [Fact]
    [Trait("Requirement", "API-12")]
    public async Task ADuplicateArrivingWhileTheFirstRuns_IsToldToRetry()
    {
        var widgets = new Widgets { Gate = new TaskCompletionSource() };
        await using var host = Host(widgets);
        using var client = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite);

        var first = SendAsync(client, "/api/v1/tests/widgets", new { }, "key-1");
        await widgets.Started.Task.WaitAsync(CancellationToken);
        using var duplicate = await SendAsync(client, "/api/v1/tests/widgets", new { }, "key-1");
        widgets.Gate.SetResult();

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await duplicate.ProblemCodeAsync()).ShouldBe("idempotency.in_progress");
        (await first).StatusCode.ShouldBe(HttpStatusCode.Created);
        widgets.Created.ShouldBe(1);
    }

    [Fact]
    [Trait("Requirement", "API-13")]
    public async Task ACrashAfterTheCommit_IsAnsweredWithTheCreatedResource_OnRetry()
    {
        var widgets = new Widgets();
        await using var host = Host(widgets);
        using var client = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite);
        await host.ExecuteSqlAsync($"""
            CREATE SEQUENCE tahsil.test_failures;
            CREATE FUNCTION tahsil.fail_first_completion() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.status = 'completed' AND nextval('tahsil.test_failures') = 1 THEN
                    RAISE EXCEPTION 'simulated crash before the response is kept';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER fail_first_completion BEFORE UPDATE ON tahsil.idempotency_records
                FOR EACH ROW EXECUTE FUNCTION tahsil.fail_first_completion();
            """);

        using var first = await SendAsync(client, "/api/v1/tests/widgets", new { }, "key-1");
        var created = await first.ReadAsync<WidgetResponse>();
        using var tooSoon = await SendAsync(client, "/api/v1/tests/widgets", new { }, "key-1");
        await host.ExecuteSqlAsync($"UPDATE tahsil.idempotency_records SET lease_expires_at = now() - interval '1 second'");
        using var retry = await SendAsync(client, "/api/v1/tests/widgets", new { }, "key-1");
        using var replay = await SendAsync(client, "/api/v1/tests/widgets", new { }, "key-1");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        tooSoon.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await retry.ReadAsync<WidgetResponse>()).Id.ShouldBe(created.Id);
        replay.Headers.GetValues(IdempotencyFilter.ReplayedHeader).ShouldBe(["true"]);
        widgets.Created.ShouldBe(1);
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.test_widgets")).ShouldBe([1]);
    }

    [Fact]
    [Trait("Requirement", "API-12")]
    public async Task ServerErrors_AreNotKept_SoTheRequestCanBeRetried()
    {
        var widgets = new Widgets { FailuresLeft = 1 };
        await using var host = Host(widgets);
        using var client = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite);

        using var failed = await SendAsync(client, "/api/v1/tests/widgets", new { }, "key-1");
        using var retried = await SendAsync(client, "/api/v1/tests/widgets", new { }, "key-1");

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        retried.StatusCode.ShouldBe(HttpStatusCode.Created);
        retried.Headers.Contains(IdempotencyFilter.ReplayedHeader).ShouldBeFalse();
        widgets.Created.ShouldBe(1);
    }

    [Fact]
    [Trait("Requirement", "API-12")]
    public async Task RequestsWithoutAKey_AreNotRecorded()
    {
        await using var host = Host(new Widgets());
        using var client = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite);

        using var response = await client.PostJsonAsync("/api/v1/tests/widgets", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.idempotency_records")).ShouldBe([0]);
    }

    private static async Task<HttpResponseMessage> SendAsync<T>(HttpClient client, string path, T body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body, options: ApiClients.Json) };
        request.Headers.TryAddWithoutValidation(IdempotencyFilter.HeaderName, key);
        return await client.SendAsync(request, CancellationToken);
    }

    private static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, string path, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(new { }) };
        request.Headers.TryAddWithoutValidation(IdempotencyFilter.HeaderName, key);
        return await client.SendAsync(request, CancellationToken);
    }

    private WebApplicationFactory<Program> Host(Widgets widgets)
    {
        var host = server.WithSettings(
            new Dictionary<string, string?> { ["ConnectionStrings:Tahsil"] = server.NewDatabase("idempotency") },
            services => services.AddSingleton(new IdempotentResourceReader(WidgetType, (_, id) =>
                Task.FromResult<IResult>(TypedResults.Created($"/api/v1/tests/widgets/{id}", new WidgetResponse(id))))),
            endpoints => endpoints.MapGroup("/api/v1/tests")
                .AddEndpointFilter<ParameterBindingFilter>()
                .AddEndpointFilter<IdempotencyFilter>()
                .MapPost("/widgets", widgets.CreateAsync)
                .RequirePermission(TahsilPermissions.ApiKeysWrite));
        host.ExecuteRawAsync("CREATE TABLE tahsil.test_widgets (id uuid PRIMARY KEY)").GetAwaiter().GetResult();
        return host;
    }

    public sealed record WidgetResponse(Guid Id);

    /// <summary>A create endpoint that records its resource for idempotency, in its own transaction.</summary>
    private sealed class Widgets
    {
        private int _created;

        public TaskCompletionSource Started { get; } = new();

        public TaskCompletionSource? Gate { get; init; }

        public int FailuresLeft { get; set; }

        public int Created => _created;

        public async Task<IResult> CreateAsync(TahsilDbContext context, IIdempotency idempotency, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            if (FailuresLeft-- > 0)
            {
                throw new InvalidOperationException("The widget factory is down.");
            }

            var id = Guid.CreateVersion7();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.Database.ExecuteSqlAsync($"INSERT INTO tahsil.test_widgets VALUES ({id})", cancellationToken);
            idempotency.RecordCreated(WidgetType, id);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            Interlocked.Increment(ref _created);

            return TypedResults.Created($"/api/v1/tests/widgets/{id}", new WidgetResponse(id));
        }
    }
}
