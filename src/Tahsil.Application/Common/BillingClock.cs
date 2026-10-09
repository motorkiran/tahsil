namespace Tahsil.Application.Common;

/// <summary>
/// The time of billing, which handlers, workers and hosted pages read: system time in Live mode, and system time
/// plus the test clock's offset in Test mode, so an installation can travel forward to see renewals and retries
/// happen. ASP.NET Core keeps the system time (the <see cref="TimeProvider"/> registration) for cookies and tokens.
/// </summary>
public abstract class BillingClock : TimeProvider;
