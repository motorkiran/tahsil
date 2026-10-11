namespace Tahsil.Domain.Invoices;

/// <summary>When failed automatic payments are tried again (DUN-1, DUN-7).</summary>
public static class DunningSchedule
{
    /// <summary>The waits between attempts after the merchant's failures, then daily (DUN-7).</summary>
    public static readonly IReadOnlyList<TimeSpan> TechnicalBackoff = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24)];

    /// <summary>The next retry of the customer schedule, as offsets from the first customer failure; none once all have passed.</summary>
    public static DateTimeOffset? NextRetry(DateTimeOffset firstFailure, IReadOnlyList<TimeSpan> offsets, DateTimeOffset now) =>
        offsets.Select(offset => firstFailure + offset).Where(at => at > now).Order().Cast<DateTimeOffset?>().FirstOrDefault();

    /// <summary>The next attempt after merchant failures since <paramref name="since"/>: the backoff's waits one after another, then daily.</summary>
    public static DateTimeOffset NextTechnicalRetry(DateTimeOffset since, DateTimeOffset now)
    {
        var at = since;
        foreach (var wait in TechnicalBackoff)
        {
            at += wait;
            if (at > now)
            {
                return at;
            }
        }

        var days = Math.Floor((now - at).TotalDays) + 1;
        return at.AddDays(days);
    }
}
