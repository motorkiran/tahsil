using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Common;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Clock;
using Tahsil.Domain.Common;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Server.Tests.Persistence;

public sealed class PlatformTableTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "FND-17")]
    public async Task Records_AreStoredAsJson_WithSnakeCaseEnums()
    {
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("json"), new FakeTimeProvider(FakeBillingClock.Start));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
            context.TestClocks.Add(TestClock.Create(FakeBillingClock.Start));
            await context.SaveChangesAsync(CancellationToken);
        }

        var stored = await host.QueryAsync<string>($"SELECT actor::text AS \"Value\" FROM tahsil.test_clock");

        stored.ShouldBe(["""{"id": null, "type": "system"}"""]);
    }

    [Fact]
    [Trait("Requirement", "FND-37")]
    public async Task TheAuditLog_RecordsTheCaller_AtTheBillingClocksTime()
    {
        var time = new FakeTimeProvider(FakeBillingClock.Start);
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("audit"), time);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IAuditLog>().Record(
                "customer.created",
                AuditSubject.Of(AuditSubjects.Customer, Guid.CreateVersion7()),
                new Dictionary<string, object?> { ["mode"] = ActorType.ApiKey });
            await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().SaveChangesAsync(CancellationToken);
        }

        await using var query = host.Services.CreateAsyncScope();
        var recorded = await query.ServiceProvider.GetRequiredService<ITahsilDbContext>().AuditEvents.SingleAsync(CancellationToken);

        recorded.ActorType.ShouldBe(ActorType.System);
        recorded.ActorId.ShouldBeNull();
        recorded.OccurredAt.ShouldBe(FakeBillingClock.Start);
        JsonNode.DeepEquals(JsonNode.Parse(recorded.Data!), JsonNode.Parse("""{"mode":"api_key"}""")).ShouldBeTrue(recorded.Data);
    }

    [Fact]
    [Trait("Requirement", "FND-20")]
    public async Task UniqueViolations_AreRecognized()
    {
        var database = await TestDbContext.CreateDatabaseAsync(server);
        var id = Guid.CreateVersion7();
        await using (var first = new TestDbContext(database))
        {
            first.Aggregates.Add(new TestAggregate { Id = id, Name = "First" });
            await first.SaveChangesAsync(CancellationToken);
        }

        await using var second = new TestDbContext(database);
        second.Aggregates.Add(new TestAggregate { Id = id, Name = "Second" });

        (await Should.ThrowAsync<DbUpdateException>(() => second.SaveChangesAsync(CancellationToken))).IsUniqueViolation().ShouldBeTrue();
    }

    [Fact]
    [Trait("Requirement", "FND-19")]
    public void AdvisoryLockKeys_SpellTahsil_AndAreDistinct()
    {
        long[] keys = [AdvisoryLockKeys.DatabasePreparation, AdvisoryLockKeys.OutboxCleanup, AdvisoryLockKeys.AuditRetention];

        keys.ShouldAllBe(key => key >> 16 == 0x544148_53494C);
        keys.Distinct().Count().ShouldBe(keys.Length);
    }
}
