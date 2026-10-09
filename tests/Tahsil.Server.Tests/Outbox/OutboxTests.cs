using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Outbox;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Server.Tests.Workers;

namespace Tahsil.Server.Tests.Outbox;

[Trait("Requirement", "FND-36")]
public sealed class OutboxTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Messages_AreDelivered_WithTheHandlerOutsideTheClaimTransaction()
    {
        var (host, handler, _) = Create();
        await using var _ = host;
        await EnqueueAsync(host, new Greeting("Ayşe"));

        (await Dispatcher(host).RunBatchAsync(CancellationToken)).ShouldBe(1);

        handler.Received.ShouldBe(["Ayşe"]);
        (await StatusesAsync(host)).ShouldBe(["processed"]);
    }

    [Fact]
    public async Task AFailedCommit_AfterTheHandlerRan_DoesNotRunItAgain()
    {
        var (host, handler, _) = Create();
        await using var _ = host;
        await host.ExecuteSqlAsync($"""
            CREATE SEQUENCE tahsil.test_failures;
            CREATE FUNCTION tahsil.fail_first_completion() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.status = 'processed' AND nextval('tahsil.test_failures') = 1 THEN
                    RAISE EXCEPTION 'simulated failure while recording the outcome';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER fail_first_completion BEFORE UPDATE ON tahsil.outbox_messages
                FOR EACH ROW EXECUTE FUNCTION tahsil.fail_first_completion();
            """);
        await EnqueueAsync(host, new Greeting("Mehmet"));

        await Dispatcher(host).RunBatchAsync(CancellationToken);

        handler.Received.ShouldBe(["Mehmet"]);
        (await StatusesAsync(host)).ShouldBe(["processed"]);
    }

    [Fact]
    public async Task AFailingHandler_IsRetriedLater()
    {
        var (host, handler, time) = Create();
        await using var _ = host;
        handler.FailuresLeft = 1;
        await EnqueueAsync(host, new Greeting("Elif"));

        await Dispatcher(host).RunBatchAsync(CancellationToken);
        (await host.QueryAsync<int>($"SELECT attempts AS \"Value\" FROM tahsil.outbox_messages")).ShouldBe([1]);
        (await Dispatcher(host).RunBatchAsync(CancellationToken)).ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(10));
        await Dispatcher(host).RunBatchAsync(CancellationToken);

        handler.Received.ShouldBe(["Elif", "Elif"]);
        (await StatusesAsync(host)).ShouldBe(["processed"]);
    }

    private (WebApplicationFactory<Program> Host, GreetingHandler Handler, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(FakeBillingClock.Start);
        var database = server.NewDatabase("outbox");
        var handler = new GreetingHandler(database);
        var host = WorkerHosts.Create(server, database, time, services =>
        {
            services.AddSingleton(new OutboxMessageTypes([typeof(Application.DependencyInjection).Assembly, typeof(OutboxTests).Assembly]));
            services.AddSingleton<IOutboxMessageHandler<Greeting>>(handler);
        });
        return (host, handler, time);
    }

    private static OutboxDispatcher Dispatcher(WebApplicationFactory<Program> host) => host.Services.GetRequiredService<OutboxDispatcher>();

    private static async Task EnqueueAsync(WebApplicationFactory<Program> host, Greeting greeting)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue(greeting);
        await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().SaveChangesAsync(CancellationToken);
    }

    private static Task<List<string>> StatusesAsync(WebApplicationFactory<Program> host) =>
        host.QueryAsync<string>($"SELECT status AS \"Value\" FROM tahsil.outbox_messages");

    [OutboxMessage("tests.greeting")]
    public sealed record Greeting(string Name);

    /// <summary>
    /// Records deliveries, and proves it runs outside the dispatcher's transaction: it takes the message rows'
    /// locks without waiting, which fails while anyone holds them.
    /// </summary>
    private sealed class GreetingHandler(string database) : IOutboxMessageHandler<Greeting>
    {
        private readonly List<string> _received = [];

        public IReadOnlyList<string> Received => _received;

        public int FailuresLeft { get; set; }

        public async Task HandleAsync(Greeting message, CancellationToken cancellationToken)
        {
            await using (var connection = new NpgsqlConnection(database))
            {
                await connection.OpenAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using var probe = new NpgsqlCommand("SELECT id FROM tahsil.outbox_messages FOR UPDATE NOWAIT", connection, transaction);
                await probe.ExecuteNonQueryAsync(cancellationToken);
            }

            _received.Add(message.Name);
            if (FailuresLeft-- > 0)
            {
                throw new InvalidOperationException("The mail server is down.");
            }
        }
    }
}
