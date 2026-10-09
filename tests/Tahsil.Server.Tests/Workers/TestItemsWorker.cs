using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Server.Tests.Workers;

/// <summary>A worker over <c>tahsil.test_items</c>, a table the tests create, that counts how often it processed each item.</summary>
internal sealed class TestItemsWorker(
    IServiceScopeFactory scopes,
    BillingClock clock,
    IOptions<WorkerOptions> options,
    WorkerSignals signals,
    ILogger<TestItemsWorker> logger,
    TestItemsWorker.Behavior behavior) : DueWorker(scopes, clock, options, signals, logger)
{
    public const string CreateTable = "CREATE TABLE tahsil.test_items (id uuid PRIMARY KEY, due_at timestamptz, processed int NOT NULL DEFAULT 0)";

    public override string Name => behavior.Leased ? "test-leased" : "test-locked";

    protected override bool CallsOtherServices => behavior.Leased;

    protected override async Task<Guid?> LockNextDueAsync(TahsilDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await context.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM tahsil.test_items WHERE due_at <= {now} ORDER BY due_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)) is [var id] ? id : null;

    protected override Task PostponeAsync(TahsilDbContext context, Guid id, DateTimeOffset until, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"UPDATE tahsil.test_items SET due_at = {until} WHERE id = {id}", cancellationToken);

    protected override async Task ProcessAsync(IServiceProvider services, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (behavior.BeforeProcessing is { } before)
        {
            await before(id, cancellationToken);
        }

        if (behavior.Failing.Contains(id))
        {
            throw new InvalidOperationException($"Item {id} is broken.");
        }

        await services.GetRequiredService<TahsilDbContext>().Database.ExecuteSqlAsync(
            $"UPDATE tahsil.test_items SET processed = processed + 1, due_at = NULL WHERE id = {id}",
            cancellationToken);
    }

    public sealed class Behavior
    {
        public bool Leased { get; init; }

        public HashSet<Guid> Failing { get; } = [];

        public Func<Guid, CancellationToken, Task>? BeforeProcessing { get; init; }
    }
}
