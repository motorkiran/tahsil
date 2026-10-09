using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Clock;
using Tahsil.Domain.Common;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class TestClockConfiguration : IEntityTypeConfiguration<TestClock>
{
    public void Configure(EntityTypeBuilder<TestClock> builder)
    {
        builder.ToTable("test_clock", table => table.HasCheckConstraint("ck_test_clock_singleton", $"id = {TestClock.SingletonId}"));
        builder.Property(clock => clock.Id).ValueGeneratedNever();
        builder.Property(clock => clock.Actor)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<Actor>(), JsonValueConverter<Actor>.Comparer);
    }
}
