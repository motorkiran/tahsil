using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Auditing;

namespace Tahsil.Server.Tests.Auditing;

public sealed class AuditApiTests(TahsilServerFixture server)
{
    [Fact]
    [Trait("Requirement", "FND-37")]
    public async Task TheTrail_CanBeFilteredByActorSubjectActionAndTime()
    {
        await using var host = Host();
        var admin = await host.CreateApiKeyAsync(TahsilPermissions.ApiKeysWrite, TahsilPermissions.AuditRead);
        using var client = host.CreateClient(admin.Key);
        var created = await (await client.PostJsonAsync("/api/v1/api-keys", new CreateApiKeyRequest { Name = "App", Permissions = [TahsilPermissions.AuditRead] }))
            .ReadAsync<CreatedApiKeyResponse>();
        (await client.PostPathAsync($"/api/v1/api-keys/{created.ApiKey.Id}/revoke")).Dispose();

        var byActor = await ListAsync(client, $"actorType=api_key&actorId={admin.ApiKey.Id}");
        var bySubject = await ListAsync(client, $"subjectType=api_key&subjectId={created.ApiKey.Id}&action=api_key.revoked");
        var bySystem = await ListAsync(client, "actorType=system");
        var future = await ListAsync(client, "from=2999-01-01T00:00:00%2B03:00");

        byActor.Items.Select(auditEvent => auditEvent.Action).ShouldBe(["api_key.revoked", "api_key.created"]);
        byActor.Items.ShouldAllBe(auditEvent => auditEvent.Actor == new ActorResponse(ActorKind.ApiKey, admin.ApiKey.Id.ToString()));
        bySubject.Items.ShouldHaveSingleItem().Data!.Value.GetProperty("name").GetString().ShouldBe("App");
        bySystem.Items.ShouldHaveSingleItem().SubjectId.ShouldBe(admin.ApiKey.Id.ToString());
        future.Items.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Requirement", "FND-37")]
    public async Task TheTrail_IsPaged_AndRejectsUnknownFilters()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(TahsilPermissions.AuditRead);
        await host.CreateApiKeyAsync(TahsilPermissions.AuditRead);

        var first = await ListAsync(client, "limit=1");
        var second = await ListAsync(client, $"limit=1&cursor={first.NextCursor}");
        using var invalid = await client.GetPathAsync("/api/v1/audit-events?actorType=robot");

        first.Items.Count.ShouldBe(1);
        second.Items.Count.ShouldBe(1);
        second.Items[0].Id.ShouldNotBe(first.Items[0].Id);
        second.NextCursor.ShouldBeNull();
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.ProblemCodeAsync()).ShouldBe("request.invalid");
    }

    [Fact]
    [Trait("Requirement", "SEC-16")]
    public async Task Retention_DeletesOldEvents_AndLogsTheDestruction_WhichItKeepsThreeYears()
    {
        await using var host = Host(new Dictionary<string, string?> { ["Tahsil:Audit:RetentionPeriod"] = "1.00:00:00" });
        await host.ExecuteSqlAsync($"""
            INSERT INTO tahsil.audit_events (id, occurred_at, action, actor_type) VALUES
                ({Guid.CreateVersion7()}, now() - interval '2 days', 'api_key.created', 'system'),
                ({Guid.CreateVersion7()}, now() - interval '3 days', 'api_key.revoked', 'system'),
                ({Guid.CreateVersion7()}, now() - interval '2 years', 'data.destroyed', 'system'),
                ({Guid.CreateVersion7()}, now() - interval '4 years', 'data.destroyed', 'system')
            """);

        (await host.Services.GetRequiredService<AuditRetention>().RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var remaining = await host.QueryAsync<string>($"""
            SELECT action || ' ' || coalesce(data->>'category', '-') || ' ' || coalesce(data->>'count', '-') AS "Value"
            FROM tahsil.audit_events ORDER BY occurred_at
            """);
        remaining.ShouldBe(["data.destroyed - -", "data.destroyed audit_events 3"]);
    }

    private static async Task<Page<AuditEventResponse>> ListAsync(HttpClient client, string query)
    {
        using var response = await client.GetPathAsync($"/api/v1/audit-events?{query}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await response.ReadAsync<Page<AuditEventResponse>>();
    }

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null)
    {
        settings ??= [];
        settings["ConnectionStrings:Tahsil"] = server.NewDatabase("audit");
        return server.WithSettings(settings);
    }
}
