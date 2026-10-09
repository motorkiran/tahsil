using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.ApiKeys;
using Tahsil.Contracts;
using Tahsil.Domain.ApiKeys;

namespace Tahsil.Server.Tests.ApiKeys;

public sealed class ApiKeyTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "API-7")]
    public async Task AValidKey_Authenticates_AndItsUseIsRecorded()
    {
        await using var host = Host();
        var key = await host.CreateApiKeyAsync(TahsilPermissions.ApiKeysRead);
        using var client = host.CreateClient(key.Key);

        using var response = await client.GetPathAsync($"/api/v1/api-keys/{key.ApiKey.Id}");
        var read = await response.ReadAsync<ApiKeyResponse>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        key.Key.Length.ShouldBe(ApiKeyFormat.Length);
        key.Key.ShouldStartWith(ApiKeyFormat.TestPrefix);
        read.DisplayPrefix.ShouldBe(key.Key[..16]);
        read.LastUsedAt.ShouldNotBeNull();
        (await host.QueryAsync<string>($"SELECT encode(secret_hash, 'hex') AS \"Value\" FROM tahsil.api_keys"))
            .ShouldBe([Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.Key)))]);
    }

    [Theory]
    [Trait("Requirement", "API-10")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("unknown")]
    [InlineData("revoked")]
    [InlineData("expired")]
    public async Task KeysThatDoNotWork_AreUnauthorized_WithoutSayingWhy(string kind)
    {
        await using var host = Host();
        var key = await host.CreateApiKeyAsync(TahsilPermissions.ApiKeysRead);
        if (kind == "revoked")
        {
            await host.ExecuteSqlAsync($"UPDATE tahsil.api_keys SET revoked_at = now()");
        }
        else if (kind == "expired")
        {
            await host.ExecuteSqlAsync($"UPDATE tahsil.api_keys SET expires_at = now() - interval '1 second'");
        }

        using var client = kind == "missing" ? host.CreateClient() : host.CreateClient(kind switch
        {
            "malformed" => "ths_test_short",
            "unknown" => ApiKeyFormat.TestPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
            _ => key.Key,
        });

        using var response = await client.GetPathAsync("/api/v1/api-keys");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        (await response.ProblemCodeAsync()).ShouldBe("auth.unauthorized");
    }

    [Fact]
    [Trait("Requirement", "API-8")]
    public async Task AKeyOfTheOtherMode_IsToldSo()
    {
        await using var host = Host();
        using var client = host.CreateClient(ApiKeyFormat.LivePrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));

        using var response = await client.GetPathAsync("/api/v1/api-keys");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ProblemCodeAsync()).ShouldBe("auth.wrong_mode");
    }

    [Fact]
    [Trait("Requirement", "API-10")]
    public async Task AMissingPermission_IsForbidden_NamingThePermission()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(TahsilPermissions.AuditRead);

        using var response = await client.GetPathAsync("/api/v1/api-keys");
        var problem = await response.ReadAsync<System.Text.Json.Nodes.JsonNode>();

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        problem["code"]!.GetValue<string>().ShouldBe("auth.forbidden");
        problem["detail"]!.GetValue<string>().ShouldContain(TahsilPermissions.ApiKeysRead);
    }

    [Fact]
    [Trait("Requirement", "API-9")]
    public async Task AKey_CannotCreateAKeyWithAPermissionItLacks()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite, TahsilPermissions.CustomersRead);

        using var escalation = await client.PostJsonAsync("/api/v1/api-keys", new CreateApiKeyRequest { Name = "More", Permissions = [TahsilPermissions.CatalogWrite] });
        using var subset = await client.PostJsonAsync("/api/v1/api-keys", new CreateApiKeyRequest { Name = "Less", Permissions = [TahsilPermissions.CustomersRead] });

        escalation.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await escalation.ProblemCodeAsync()).ShouldBe("api_key.permission_not_held");
        subset.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await subset.ReadAsync<CreatedApiKeyResponse>();
        subset.Headers.Location!.OriginalString.ShouldBe($"/api/v1/api-keys/{created.ApiKey.Id}");
        created.ApiKey.Permissions.ShouldBe([TahsilPermissions.CustomersRead]);
    }

    [Fact]
    [Trait("Requirement", "API-8")]
    public async Task EveryWrite_IsAuditedWithTheKeysId_AndARevokedKeyStopsWorking()
    {
        await using var host = Host();
        var admin = await host.CreateApiKeyAsync(TahsilPermissions.ApiKeysWrite, TahsilPermissions.ApiKeysRead);
        using var client = host.CreateClient(admin.Key);

        var created = await (await client.PostJsonAsync("/api/v1/api-keys", new CreateApiKeyRequest { Name = "App", Permissions = [TahsilPermissions.ApiKeysRead] }))
            .ReadAsync<CreatedApiKeyResponse>();
        using var revoked = await client.PostPathAsync($"/api/v1/api-keys/{created.ApiKey.Id}/revoke");
        using var afterwards = await host.CreateClient(created.Key).GetPathAsync("/api/v1/api-keys");

        (await revoked.ReadAsync<ApiKeyResponse>()).RevokedAt.ShouldNotBeNull();
        afterwards.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var events = await host.QueryAsync<string>($"""
            SELECT action || ' ' || actor_type || ' ' || actor_id AS "Value" FROM tahsil.audit_events
            WHERE subject_id = {created.ApiKey.Id.ToString()} ORDER BY occurred_at, id
            """);
        events.ShouldBe([$"api_key.created api_key {admin.ApiKey.Id}", $"api_key.revoked api_key {admin.ApiKey.Id}"]);
        created.ApiKey.CreatedBy.ShouldBe(new ActorResponse(ActorKind.ApiKey, admin.ApiKey.Id.ToString()));
    }

    [Fact]
    [Trait("Requirement", "API-8")]
    public async Task AnInstallation_HasAtMostFiftyActiveKeys()
    {
        await using var host = Host();
        for (var count = 0; count < ApiKey.MaxActiveKeys; count++)
        {
            await host.CreateApiKeyAsync(TahsilPermissions.AuditRead);
        }

        await using var scope = host.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<CreateApiKeyHandler>()
            .HandleAsync(new CreateApiKeyRequest { Name = "One too many", Permissions = [TahsilPermissions.AuditRead] }, CancellationToken);

        result.Error.ShouldBe(ApiKeyErrors.LimitReached);
    }

    [Fact]
    [Trait("Requirement", "FND-28")]
    public async Task OnlyTahsilPermissions_CanBeGiven_AndRequestsAreValidated()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysWrite);

        using var unknown = await client.PostJsonAsync("/api/v1/api-keys", new CreateApiKeyRequest { Name = "Odd", Permissions = ["kimlik.users:read"] });
        using var nameless = await client.PostJsonAsync("/api/v1/api-keys", new { permissions = new[] { TahsilPermissions.ApiKeysWrite } });
        using var longName = await client.PostJsonAsync("/api/v1/api-keys", new CreateApiKeyRequest { Name = new string('x', 101), Permissions = [TahsilPermissions.ApiKeysWrite] });
        var validation = await longName.ReadAsync<System.Text.Json.Nodes.JsonNode>();

        (await unknown.ProblemCodeAsync()).ShouldBe("api_key.unknown_permission");
        nameless.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await nameless.ProblemCodeAsync()).ShouldBe("request.invalid");
        longName.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        validation["code"]!.GetValue<string>().ShouldBe("request.invalid");
        validation["errors"]!["Name"].ShouldNotBeNull();
    }

    private WebApplicationFactory<Program> Host() =>
        server.WithSettings(new Dictionary<string, string?> { ["ConnectionStrings:Tahsil"] = server.NewDatabase("apikeys") });
}
