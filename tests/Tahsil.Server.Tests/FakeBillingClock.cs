using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Common;

namespace Tahsil.Server.Tests;

/// <summary>A billing clock that tests move by hand.</summary>
internal sealed class FakeBillingClock(FakeTimeProvider time) : BillingClock
{
    /// <summary>A fixed starting point, so failures reproduce.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => time.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => time.LocalTimeZone;

    public override long TimestampFrequency => time.TimestampFrequency;

    public override long GetTimestamp() => time.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        time.CreateTimer(callback, state, dueTime, period);
}
