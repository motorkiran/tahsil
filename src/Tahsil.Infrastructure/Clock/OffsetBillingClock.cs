using Tahsil.Application.Common;

namespace Tahsil.Infrastructure.Clock;

/// <summary>
/// System time plus the test clock offset, which only Test mode ever sets. Durations (timestamps and timers) are
/// the system's: time travel moves the date, not the speed of time.
/// </summary>
internal sealed class OffsetBillingClock(TimeProvider system, TestClockOffset offset) : BillingClock
{
    public override DateTimeOffset GetUtcNow() => system.GetUtcNow() + offset.Current;

    public override TimeZoneInfo LocalTimeZone => system.LocalTimeZone;

    public override long TimestampFrequency => system.TimestampFrequency;

    public override long GetTimestamp() => system.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        system.CreateTimer(callback, state, dueTime, period);
}
