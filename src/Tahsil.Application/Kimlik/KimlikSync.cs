using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Kimlik;

/// <summary>Brings a subscription's Kimlik subscription in line with it (KML-16); level-triggered, so repeats converge.</summary>
[OutboxMessage("kimlik.sync_subscription")]
public sealed record SyncKimlikSubscription(Guid SubscriptionId);

/// <summary>A sync that failed; the outbox tries the message again with backoff (KML-20).</summary>
public sealed class KimlikSyncException(string message) : Exception(message);

/// <summary>
/// Syncs one subscription to Kimlik (KML-17): computes the desired state, reads the Kimlik subscription it mirrors to,
/// and creates, adopts, changes or ends it with one request per field set, observing Kimlik's rules. Instants
/// compared with Kimlik's state and the "now" that ends access are Kimlik's real time, not the billing clock, which
/// time travel moves in Test mode. Billing never waits for this.
/// </summary>
public sealed class KimlikSynchronizer(
    ITahsilDbContext context,
    IKimlikClient kimlik,
    Attention attention,
    IOptions<KimlikOptions> options,
    IOptions<DunningOptions> dunning,
    TimeProvider time,
    BillingClock clock) : IOutboxMessageHandler<SyncKimlikSubscription>
{
    public const string ManagedElsewhere = "The Kimlik subscriber has a subscription managed elsewhere.";

    private static readonly TimeSpan AttentionAfter = TimeSpan.FromHours(24);

    public async Task HandleAsync(SyncKimlikSubscription message, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled
            || await context.Subscriptions.AsNoTracking().Include(row => row.Items).SingleOrDefaultAsync(row => row.Id == message.SubscriptionId, cancellationToken)
                is not { } subscription)
        {
            return;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
        var desired = KimlikStates.Desired(await FactsAsync(subscription, customer, cancellationToken), options.Value);
        var mirror = await context.KimlikMirrors.SingleOrDefaultAsync(row => row.SubscriptionId == subscription.Id, cancellationToken);
        if (mirror is null)
        {
            if (!desired.GivesPlan)
            {
                // Never mirrored, and nothing to give.
                return;
            }

            mirror = KimlikMirror.For(subscription.Id);
            context.KimlikMirrors.Add(mirror);
        }

        var synced = await SyncAsync(subscription, customer.KimlikSubscriber, mirror.KimlikSubscriptionId, desired, cancellationToken);
        var now = clock.GetUtcNow();
        if (synced.IsSuccess)
        {
            mirror.Succeeded(synced.Value, now);
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        mirror.Failed(synced.Error!.Message, now);
        var conflict = synced.Error.Code == KimlikSyncErrors.ManagedElsewhere.Code;
        if (conflict || now - mirror.FailingSince >= AttentionAfter)
        {
            await attention.RaiseAsync(
                conflict ? AttentionKind.KimlikConflict : AttentionKind.KimlikSyncFailing,
                AuditSubjects.Subscription,
                subscription.Id,
                subscription.CustomerId,
                new Dictionary<string, string> { ["error"] = mirror.Error! },
                now,
                cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        if (!conflict)
        {
            // A conflict waits for a person; reconciliation looks again.
            throw new KimlikSyncException($"Subscription {subscription.Id} did not sync to Kimlik: {synced.Error.Code}.");
        }
    }

    /// <summary>The subscription's facts: the plan of its product with a Kimlik plan (SUB-2), and dunning's final action (KML-15).</summary>
    private async Task<KimlikFacts> FactsAsync(Subscription subscription, Customer customer, CancellationToken cancellationToken)
    {
        var prices = subscription.Items.Select(item => item.PriceId).ToList();
        var plan = await context.Prices.Where(price => prices.Contains(price.Id))
            .Join(context.Products, price => price.ProductId, product => product.Id, (_, product) => product.KimlikPlan)
            .Where(key => key != null)
            .OrderBy(key => key)
            .FirstOrDefaultAsync(cancellationToken);
        DateTimeOffset? finalAction = null;
        if (subscription.Status == SubscriptionStatus.PastDue
            && await context.Invoices.Where(invoice => invoice.SubscriptionId == subscription.Id && invoice.Status == InvoiceStatus.Open && invoice.DunningStartedAt != null)
                .MinAsync(invoice => invoice.DunningStartedAt, cancellationToken) is { } firstFailure)
        {
            finalAction = firstFailure + dunning.Value.Schedule[^1];
        }

        return new KimlikFacts(subscription.Status, subscription.TrialEnd, subscription.CurrentPeriodEnd, subscription.CancelAt, plan, customer.KimlikSubscriber is not null, finalAction);
    }

    /// <summary>KML-17's steps; returns the Kimlik subscription that mirrors the subscription now, if any.</summary>
    private async Task<Result<Guid?>> SyncAsync(
        Subscription subscription, KimlikSubscriber? subscriber, Guid? stored, KimlikDesired desired, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        KimlikSubscription? actual = null;
        if (stored is { } storedId)
        {
            var read = await kimlik.GetSubscriptionAsync(storedId, cancellationToken);
            if (read.IsSuccess)
            {
                actual = read.Value;
            }
            else if (read.Error!.Type != ErrorType.NotFound)
            {
                return read.Error;
            }
        }

        // An end that has passed in Kimlik's time gives nothing any more.
        if (!desired.GivesPlan || desired.At <= now)
        {
            if (actual is not null && actual.IsInEffectAt(now) && await EndAsync(actual, now, cancellationToken) is { IsFailure: true } notEnded)
            {
                return notEnded.Error!;
            }

            return (Guid?)null;
        }

        var reference = subscription.Id.ToString();
        if (actual is null || actual.Status == KimlikSubscriptionStatus.Expired)
        {
            var created = await kimlik.CreateSubscriptionAsync(
                new KimlikSubscriptionDraft(
                    subscriber!,
                    desired.Plan!,
                    desired.Kind == KimlikDesiredKind.Trialing ? desired.At : null,
                    desired.Kind == KimlikDesiredKind.Trialing ? null : desired.At,
                    reference),
                cancellationToken);
            if (created.IsSuccess)
            {
                actual = created.Value;
            }
            else if (created.Error!.Code == "subscription.exists")
            {
                var adopted = await AdoptAsync(subscriber!, reference, cancellationToken);
                if (adopted.IsFailure)
                {
                    return adopted.Error!;
                }

                actual = adopted.Value;
            }
            else
            {
                return created.Error;
            }
        }

        var converged = await ConvergeAsync(actual, desired, reference, cancellationToken);
        return converged.IsFailure ? converged.Error! : actual.Id;
    }

    /// <summary>The subscriber's current Kimlik subscription, when it is this subscription's or nobody's (KML-17 step 4).</summary>
    private async Task<Result<KimlikSubscription>> AdoptAsync(KimlikSubscriber subscriber, string reference, CancellationToken cancellationToken)
    {
        var listed = await kimlik.ListSubscriptionsAsync(subscriber, cancellationToken);
        if (listed.IsFailure)
        {
            return listed.Error!;
        }

        return listed.Value.FirstOrDefault(candidate => candidate.Status != KimlikSubscriptionStatus.Expired) switch
        {
            // Expired between the two requests: the next attempt creates it.
            null => KimlikErrors.Unavailable,
            { ExternalReference: { } other } when other != reference => KimlikSyncErrors.ManagedElsewhere,
            var current => current,
        };
    }

    /// <summary>One change with the fields that differ, then a cancellation when access ends with the period (KML-17 step 5).</summary>
    private async Task<Result> ConvergeAsync(KimlikSubscription actual, KimlikDesired desired, string reference, CancellationToken cancellationToken)
    {
        var at = desired.At!.Value;
        var change = new KimlikSubscriptionChange
        {
            Plan = actual.Plan != desired.Plan ? desired.Plan : null,
            ExternalReference = actual.ExternalReference != reference ? reference : null,
        };
        change = desired.Kind switch
        {
            KimlikDesiredKind.Trialing => change with
            {
                Status = actual.Status != KimlikSubscriptionStatus.Trialing ? KimlikSubscriptionStatus.Trialing : null,
                TrialEndsAt = actual.TrialEndsAt != at || actual.Status != KimlikSubscriptionStatus.Trialing ? at : null,
            },

            // A trial converts, and a canceled subscription resumes, by its status, with the period in the same request.
            KimlikDesiredKind.Active => change with
            {
                Status = actual.Status != KimlikSubscriptionStatus.Active ? KimlikSubscriptionStatus.Active : null,
                CurrentPeriodEnd = actual.CurrentPeriodEnd != at || actual.Status == KimlikSubscriptionStatus.Trialing ? at : null,
            },
            _ => change with
            {
                Status = actual.Status == KimlikSubscriptionStatus.Trialing ? KimlikSubscriptionStatus.Active : null,
                CurrentPeriodEnd = actual.CurrentPeriodEnd != at || actual.Status == KimlikSubscriptionStatus.Trialing ? at : null,
            },
        };

        var status = actual.Status;
        if (!change.IsEmpty)
        {
            var changed = await kimlik.UpdateSubscriptionAsync(actual.Id, change, cancellationToken);
            if (changed.IsFailure)
            {
                return changed.Error!;
            }

            status = changed.Value.Status;
        }

        if (desired.Kind == KimlikDesiredKind.CancelsAt && status == KimlikSubscriptionStatus.Active)
        {
            var canceled = await kimlik.CancelSubscriptionAsync(actual.Id, cancellationToken);
            return canceled.IsFailure ? canceled.Error! : Result.Success();
        }

        return Result.Success();
    }

    /// <summary>
    /// Ends a Kimlik subscription now (KML-18): its period ends now, which takes it out of effect at once, and it is
    /// canceled when Kimlik still allows it; a trial is ended by canceling it.
    /// </summary>
    private async Task<Result> EndAsync(KimlikSubscription actual, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (actual.Status != KimlikSubscriptionStatus.Trialing)
        {
            var ended = await kimlik.UpdateSubscriptionAsync(actual.Id, new KimlikSubscriptionChange { CurrentPeriodEnd = now }, cancellationToken);
            if (ended.IsFailure)
            {
                return ended.Error!;
            }

            if (ended.Value.Status == KimlikSubscriptionStatus.Canceled)
            {
                return Result.Success();
            }
        }

        // Out of effect already, unless it was a trial: a failure here leaves it so, which is the goal.
        var canceled = await kimlik.CancelSubscriptionAsync(actual.Id, cancellationToken);
        return canceled.IsFailure && actual.Status == KimlikSubscriptionStatus.Trialing ? canceled.Error! : Result.Success();
    }
}

public static class KimlikSyncErrors
{
    public static readonly Error ManagedElsewhere = Error.Conflict("kimlik.managed_elsewhere", KimlikSynchronizer.ManagedElsewhere);
}
