using Microsoft.EntityFrameworkCore;
using Tahsil.Domain.Common;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Server.Tests.Persistence;

public enum TestStatus
{
    Active,
    PastDue,
}

public sealed class TestAggregate : AggregateRoot
{
    public required Guid Id { get; init; }

    public required string Name { get; set; }

    public TestStatus Status { get; set; }

    public TestStatus? PreviousStatus { get; set; }

    public DateTimeOffset At { get; set; }

    public long Rank { get; set; }
}

/// <summary>A model of its own with Tahsil's conventions, in a database of its own.</summary>
internal sealed class TestDbContext(string connectionString) : DbContext
{
    public DbSet<TestAggregate> Aggregates => Set<TestAggregate>();

    public static async Task<string> CreateDatabaseAsync(TahsilServerFixture server)
    {
        var connectionString = server.NewDatabase("conventions");
        await using var context = new TestDbContext(connectionString);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return connectionString;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder
        .UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        PersistenceConventions.Configure(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestAggregate>().Property(aggregate => aggregate.Id).ValueGeneratedNever();
        PersistenceConventions.Apply(modelBuilder);
    }
}
