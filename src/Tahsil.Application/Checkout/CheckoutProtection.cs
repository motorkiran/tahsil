using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Checkout;
using Tahsil.Domain.Common;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Checkout;

/// <summary>
/// Card testing protection (CHK-21, CHK-22): limits on payment attempts per session, network and customer, checked
/// before the provider is called; and an alert when a connection's checkout payments start failing in bulk.
/// </summary>
public sealed class CheckoutProtection(ITahsilDbContext context, Attention attention, IOptions<CheckoutPageOptions> options)
{
    /// <summary>The window and thresholds of a decline spike (CHK-22).</summary>
    public static readonly TimeSpan SpikeWindow = TimeSpan.FromMinutes(15);

    public const int SpikeMinimumAttempts = 20;

    private static readonly Counter<long> Spikes = TahsilDiagnostics.Meter.CreateCounter<long>(
        "tahsil.checkout.decline_spikes", unit: "{spike}", description: "Decline spikes on checkout payments, by connection (CHK-22).");

    /// <summary>The network an attempt comes from: an IPv4 address, or the /64 of an IPv6 address.</summary>
    public static string? NetworkOf(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return string.Create(CultureInfo.InvariantCulture, $"{new IPAddress(bytes)}/64");
    }

    /// <summary>Whether another attempt may start (CHK-21), checked under the session's lock before the payment is written.</summary>
    public async Task<Error?> CheckAttemptAsync(CheckoutSession session, Guid customerId, string? network, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var limits = options.Value;
        if (session.PaymentAttempts >= limits.MaxPaymentAttempts)
        {
            return CheckoutErrors.AttemptsExceeded;
        }

        var hourAgo = now.AddHours(-1);
        if (network is not null
            && await context.Payments.CountAsync(payment => payment.ClientNetwork == network && payment.CreatedAt > hourAgo, cancellationToken) >= limits.MaxAttemptsPerAddressPerHour)
        {
            return CheckoutErrors.AttemptsExceeded;
        }

        var dayAgo = now.AddDays(-1);
        return await context.Payments.CountAsync(
            payment => payment.CustomerId == customerId && payment.CheckoutSessionId != null && payment.Status == PaymentStatus.Failed && payment.FailedAt > dayAgo,
            cancellationToken) >= limits.MaxFailedAttemptsPerCustomerPerDay
            ? CheckoutErrors.AttemptsExceeded
            : null;
    }

    /// <summary>
    /// After a checkout payment failed: more than half of the connection's checkout payments that ended in the last 15
    /// minutes failed, out of at least 20, raises an attention item and a metric (CHK-22). An operator may then pause
    /// new checkouts on the connection.
    /// </summary>
    public async Task WatchDeclinesAsync(Payment failed, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (failed.CheckoutSessionId is null || failed.ConnectionId is not { } connectionId)
        {
            return;
        }

        var since = now - SpikeWindow;
        var ended = await context.Payments.AsNoTracking()
            .Where(payment => payment.ConnectionId == connectionId && payment.CheckoutSessionId != null
                && ((payment.Status == PaymentStatus.Failed && payment.FailedAt > since) || (payment.Status == PaymentStatus.Succeeded && payment.SucceededAt > since)))
            .GroupBy(payment => payment.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        // The failure being recorded is not saved yet.
        var failures = ended.Where(group => group.Status == PaymentStatus.Failed).Sum(group => group.Count) + 1;
        var total = ended.Sum(group => group.Count) + 1;
        if (total < SpikeMinimumAttempts || failures * 2 <= total)
        {
            return;
        }

        Spikes.Add(1, new KeyValuePair<string, object?>("connection", connectionId));
        await attention.RaiseAsync(AttentionKind.DeclineSpike, AuditSubjects.Connection, connectionId, null, new Dictionary<string, string>
        {
            ["failed"] = failures.ToString(CultureInfo.InvariantCulture),
            ["attempts"] = total.ToString(CultureInfo.InvariantCulture),
            ["windowMinutes"] = SpikeWindow.TotalMinutes.ToString(CultureInfo.InvariantCulture),
        }, now, cancellationToken);
    }
}
