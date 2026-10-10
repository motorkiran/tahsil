using Tahsil.Domain.Common;

namespace Tahsil.Domain.Clock;

/// <summary>
/// How far a Test mode installation has traveled in time. The billing clock adds the offset to the system time; it
/// only ever grows. There is one row.
/// </summary>
public sealed class TestClock
{
    public const short SingletonId = 1;

    private TestClock()
    {
    }

    public short Id { get; private set; } = SingletonId;

    public long OffsetSeconds { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Actor Actor { get; private set; } = Actor.System;

    public static TestClock Create(DateTimeOffset now) => new() { UpdatedAt = now };

    /// <summary>Moves the clock forward to <paramref name="offsetSeconds"/>; it never goes back.</summary>
    public void AdvanceTo(long offsetSeconds, Actor actor, DateTimeOffset now)
    {
        if (offsetSeconds < OffsetSeconds)
        {
            throw new InvalidOperationException("The test clock only moves forward.");
        }

        OffsetSeconds = offsetSeconds;
        Actor = actor;
        UpdatedAt = now;
    }
}
