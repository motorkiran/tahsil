using Tahsil.Application.Common;

namespace Tahsil.Server.Tests.Persistence;

[Trait("Requirement", "FND-21")]
public sealed class PaginationTests(TahsilServerFixture server)
{
    private static readonly Keyset<TestAggregate, long> ByRank = new("rank", aggregate => aggregate.Rank, aggregate => aggregate.Id);

    private static readonly Keyset<TestAggregate, DateTimeOffset> Newest =
        new("newest", aggregate => aggregate.At, aggregate => aggregate.Id, Descending: true);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pages_CoverEveryRowOnce_InOrder_WithTies()
    {
        var (database, rows) = await SeedAsync(500);
        await using var context = new TestDbContext(database);

        var ascending = await ReadAllAsync(context, ByRank, limit: 37);
        var descending = await ReadAllAsync(context, Newest, limit: 50);

        ascending.ShouldBe([.. rows.OrderBy(row => row.Rank).ThenBy(row => row.Id).Select(row => row.Id)]);
        descending.ShouldBe([.. rows.OrderByDescending(row => row.At).ThenByDescending(row => row.Id).Select(row => row.Id)]);
    }

    [Fact]
    public async Task TheLastPage_HasNoCursor()
    {
        var (database, _) = await SeedAsync(3);
        await using var context = new TestDbContext(database);

        var page = await ByRank.ReadAsync(context.Aggregates, null, Paging.DefaultLimit, CancellationToken);

        page.ShouldNotBeNull().Items.Count.ShouldBe(3);
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Cursors_FromAnotherOrder_OrTampered_AreRejected()
    {
        var (database, _) = await SeedAsync(10);
        await using var context = new TestDbContext(database);
        var cursor = (await ByRank.ReadAsync(context.Aggregates, null, 2, CancellationToken))!.NextCursor!;

        (await Newest.ReadAsync(context.Aggregates, cursor, 2, CancellationToken)).ShouldBeNull();
        (await ByRank.ReadAsync(context.Aggregates, "not a cursor", 2, CancellationToken)).ShouldBeNull();
        (await ByRank.ReadAsync(context.Aggregates, cursor[..^4], 2, CancellationToken)).ShouldBeNull();
        (await ByRank.ReadAsync(context.Aggregates, new Cursor("rank", "seven", Guid.CreateVersion7()).Encode(), 2, CancellationToken)).ShouldBeNull();
    }

    private static async Task<List<Guid>> ReadAllAsync<TKey>(TestDbContext context, Keyset<TestAggregate, TKey> keyset, int limit)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await keyset.ReadAsync(context.Aggregates, cursor, limit, CancellationToken)).ShouldNotBeNull();
            page.Items.Count.ShouldBeLessThanOrEqualTo(limit);
            ids.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return ids;
    }

    /// <summary>Rows whose sort keys repeat, so ties are broken by ID across page boundaries.</summary>
    private async Task<(string Database, List<TestAggregate> Rows)> SeedAsync(int count)
    {
        var database = await TestDbContext.CreateDatabaseAsync(server);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, count).Select(index => new TestAggregate
        {
            Id = Guid.CreateVersion7(start.AddSeconds(count - index)),
            Name = $"Row {index}",
            Rank = index % 17,
            At = start.AddMinutes(index % 23),
        }).ToList();

        await using var context = new TestDbContext(database);
        context.Aggregates.AddRange(rows);
        await context.SaveChangesAsync(CancellationToken);
        return (database, rows);
    }
}
