using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Server.Tests.Workers;

public sealed class DueWorkerTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Requirement", "ENG-3")]
    [Trait("Requirement", "ENG-4")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoInstances_ShareTheWork_WithoutProcessingAnItemTwice(bool leased)
    {
        var time = new FakeTimeProvider(FakeBillingClock.Start);
        var database = server.NewDatabase("workers");
        await using var first = WorkerHosts.Create(server, database, time);
        await using var second = WorkerHosts.Create(server, database, time);
        await first.ExecuteRawAsync(TestItemsWorker.CreateTable);
        await AddItemsAsync(first, time, 200);
        var behavior = new TestItemsWorker.Behavior { Leased = leased, BeforeProcessing = (_, _) => Task.Delay(1, CancellationToken) };

        await Task.WhenAll(DrainAsync(Worker(first, behavior)), DrainAsync(Worker(second, behavior)));

        var counts = await first.QueryAsync<int>($"SELECT processed AS \"Value\" FROM tahsil.test_items");
        counts.Count.ShouldBe(200);
        counts.ShouldAllBe(count => count == 1);
    }

    [Fact]
    [Trait("Requirement", "ENG-4")]
    public async Task AnItemLeasedByADeadInstance_IsResumed_WhenTheLeaseRunsOut()
    {
        var time = new FakeTimeProvider(FakeBillingClock.Start);
        var database = server.NewDatabase("leases");
        await using var dying = WorkerHosts.Create(server, database, time);
        await using var survivor = WorkerHosts.Create(server, database, time);
        await dying.ExecuteRawAsync(TestItemsWorker.CreateTable);
        await AddItemsAsync(dying, time, 1);

        var claimed = new TaskCompletionSource();
        using var crash = new CancellationTokenSource();
        var hanging = Worker(dying, new TestItemsWorker.Behavior
        {
            Leased = true,
            BeforeProcessing = async (_, token) =>
            {
                claimed.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        });
        var run = hanging.RunBatchAsync(crash.Token);
        await claimed.Task.WaitAsync(CancellationToken);
        await crash.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => run);

        var resumer = Worker(survivor, new TestItemsWorker.Behavior { Leased = true });
        (await resumer.RunBatchAsync(CancellationToken)).ShouldBe(0);
        time.Advance(TimeSpan.FromMinutes(5));
        (await resumer.RunBatchAsync(CancellationToken)).ShouldBe(1);

        (await survivor.QueryAsync<int>($"SELECT processed AS \"Value\" FROM tahsil.test_items")).ShouldBe([1]);
    }

    [Theory]
    [Trait("Requirement", "ENG-6")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailingItem_BacksOff_IsReportedAfterTenFailures_AndDoesNotBlockOthers(bool leased)
    {
        var time = new FakeTimeProvider(FakeBillingClock.Start);
        await using var host = WorkerHosts.Create(server, server.NewDatabase("failures"), time);
        await host.ExecuteRawAsync(TestItemsWorker.CreateTable);
        var ids = await AddItemsAsync(host, time, 3);
        var broken = ids[0];
        var behavior = new TestItemsWorker.Behavior { Leased = leased };
        behavior.Failing.Add(broken);
        var worker = Worker(host, behavior);

        (await worker.RunBatchAsync(CancellationToken)).ShouldBe(3);
        (await host.QueryAsync<int>($"SELECT processed AS \"Value\" FROM tahsil.test_items WHERE id <> {broken}")).ShouldBe([1, 1]);

        for (var failures = 1; failures <= 10; failures++)
        {
            var dueAt = (await host.QueryAsync<DateTimeOffset>($"SELECT due_at AS \"Value\" FROM tahsil.test_items WHERE id = {broken}")).Single();
            var count = (await host.QueryAsync<int>($"SELECT failure_count AS \"Value\" FROM tahsil.worker_failures WHERE item_id = {broken}")).Single();
            count.ShouldBe(failures);
            dueAt.ShouldBe(time.GetUtcNow() + WorkerFailure.BackoffAfter(failures));

            time.Advance(WorkerFailure.BackoffAfter(failures) - TimeSpan.FromSeconds(1));
            (await worker.RunBatchAsync(CancellationToken)).ShouldBe(0);
            time.Advance(TimeSpan.FromSeconds(1));
            if (failures < 10)
            {
                (await worker.RunBatchAsync(CancellationToken)).ShouldBe(1);
            }
        }

        (await host.QueryAsync<DateTimeOffset?>($"SELECT attention_raised_at AS \"Value\" FROM tahsil.worker_failures WHERE item_id = {broken}"))
            .Single().ShouldNotBeNull();
        (await host.QueryAsync<string>($"SELECT action AS \"Value\" FROM tahsil.audit_events WHERE subject_id = {broken.ToString()}"))
            .ShouldBe(["worker.attention_raised"]);

        behavior.Failing.Clear();
        (await worker.RunBatchAsync(CancellationToken)).ShouldBe(1);
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.worker_failures")).ShouldBe([0]);
    }

    private static TestItemsWorker Worker(WebApplicationFactory<Program> host, TestItemsWorker.Behavior behavior) =>
        ActivatorUtilities.CreateInstance<TestItemsWorker>(host.Services, behavior);

    private static async Task DrainAsync(TestItemsWorker worker)
    {
        while (await worker.RunBatchAsync(CancellationToken) > 0)
        {
        }
    }

    private static async Task<List<Guid>> AddItemsAsync(WebApplicationFactory<Program> host, FakeTimeProvider time, int count)
    {
        var due = time.GetUtcNow();
        var ids = Enumerable.Range(0, count).Select(index => Guid.CreateVersion7(due.AddMilliseconds(index))).ToList();
        foreach (var id in ids)
        {
            await host.ExecuteSqlAsync($"INSERT INTO tahsil.test_items (id, due_at) VALUES ({id}, {due})");
        }

        return ids;
    }
}
