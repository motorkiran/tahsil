using Microsoft.EntityFrameworkCore;

namespace Tahsil.Server.Tests.Persistence;

public sealed class ConventionTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "FND-14")]
    public async Task ConcurrentUpdates_OfOneAggregate_Conflict()
    {
        var database = await TestDbContext.CreateDatabaseAsync(server);
        var id = await AddAsync(database, new TestAggregate { Id = Guid.CreateVersion7(), Name = "Original" });

        await using var first = new TestDbContext(database);
        await using var second = new TestDbContext(database);
        var mine = await first.Aggregates.SingleAsync(aggregate => aggregate.Id == id, CancellationToken);
        var theirs = await second.Aggregates.SingleAsync(aggregate => aggregate.Id == id, CancellationToken);

        theirs.Name = "Theirs";
        await second.SaveChangesAsync(CancellationToken);
        mine.Name = "Mine";

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => first.SaveChangesAsync(CancellationToken));
        theirs.Version.ShouldNotBe(0u);
    }

    [Fact]
    [Trait("Requirement", "FND-15")]
    public async Task Timestamps_WithAnOffset_AreStoredAsTheSameInstantInUtc()
    {
        var database = await TestDbContext.CreateDatabaseAsync(server);
        var local = new DateTimeOffset(2026, 10, 9, 12, 30, 0, TimeSpan.FromHours(3));
        var id = await AddAsync(database, new TestAggregate { Id = Guid.CreateVersion7(), Name = "Clock", At = local });

        await using var context = new TestDbContext(database);
        var stored = await context.Aggregates.SingleAsync(aggregate => aggregate.Id == id, CancellationToken);

        stored.At.ShouldBe(local);
        stored.At.Offset.ShouldBe(TimeSpan.Zero);
        stored.At.UtcDateTime.ShouldBe(new DateTime(2026, 10, 9, 9, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    [Trait("Requirement", "FND-13")]
    public async Task Enums_AreStoredAsSnakeCaseStrings()
    {
        var database = await TestDbContext.CreateDatabaseAsync(server);
        var id = await AddAsync(database, new TestAggregate
        {
            Id = Guid.CreateVersion7(),
            Name = "Late",
            Status = TestStatus.PastDue,
            PreviousStatus = TestStatus.Active,
        });

        await using var context = new TestDbContext(database);
        var stored = await context.Database
            .SqlQuery<string>($"SELECT status || '/' || previous_status AS \"Value\" FROM aggregates WHERE id = {id}")
            .SingleAsync(CancellationToken);
        var loaded = await context.Aggregates.SingleAsync(aggregate => aggregate.Status == TestStatus.PastDue, CancellationToken);

        stored.ShouldBe("past_due/active");
        loaded.PreviousStatus.ShouldBe(TestStatus.Active);
    }

    private static async Task<Guid> AddAsync(string database, TestAggregate aggregate)
    {
        await using var context = new TestDbContext(database);
        context.Aggregates.Add(aggregate);
        await context.SaveChangesAsync(CancellationToken);
        return aggregate.Id;
    }
}
