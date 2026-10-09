using Microsoft.EntityFrameworkCore;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Clock;

/// <summary>
/// The test clock's offset as this instance last read it. Time travel on this instance updates it at once; the
/// refresher picks up travel on other instances within a second.
/// </summary>
internal sealed class TestClockOffset
{
    private long _seconds;

    public TimeSpan Current => TimeSpan.FromSeconds(Volatile.Read(ref _seconds));

    public void Set(long seconds) => Volatile.Write(ref _seconds, seconds);

    public async Task RefreshAsync(TahsilDbContext context, CancellationToken cancellationToken) =>
        Set(await context.TestClocks.AsNoTracking().Select(clock => clock.OffsetSeconds).FirstOrDefaultAsync(cancellationToken));
}
