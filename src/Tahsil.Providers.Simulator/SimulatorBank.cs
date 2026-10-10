using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Simulator;

public enum CardPageStep
{
    CardEntry,
    Authentication,
    Finished,
}

/// <summary>What the card page shows for a token.</summary>
public sealed record CardPage(CardPageStep Step, long Amount, string Currency, int Exponent, PaymentPurpose Purpose, bool AskToSave, string? CardLast4, Uri? RedirectUrl);

/// <summary>What the customer typed on the card page.</summary>
/// <param name="Number">The card number.</param>
/// <param name="Expiry">The expiry as <c>MM/YY</c>.</param>
/// <param name="Cvc">The security code.</param>
/// <param name="SaveCard">Whether the customer agreed to save the card, where the page asks.</param>
public sealed record CardEntryForm(string? Number, string? Expiry, string? Cvc, bool SaveCard);

public enum CardEntryError
{
    NotATestCard,
    InvalidExpiry,
    InvalidCvc,
}

public enum AuthenticationDecision
{
    Approve,
    Decline,

    /// <summary>The customer leaves the bank's page: the payment is abandoned.</summary>
    DoNothing,
}

public abstract record CardPageResult
{
    public sealed record NotFound : CardPageResult;

    public sealed record Invalid(CardEntryError Error) : CardPageResult;

    public sealed record Authenticate : CardPageResult;

    /// <summary>The payment is over; the page sends the customer back to the merchant.</summary>
    public sealed record Redirect(Uri Url) : CardPageResult;
}

/// <summary>
/// The simulated provider behind the simulator adapter and its card page: payments, 3D Secure, saved cards and
/// refunds, answering as the test cards say (SIM §3). Time is the billing clock, so time travel moves it too.
/// </summary>
public sealed class SimulatorBank(ISimulatorLedger ledger, TimeProvider clock, ISimulatorNotifier? notifier = null)
{
    public const string SignatureHeader = "Simulator-Signature";

    private const int Attempts = 5;
    private static readonly TimeSpan PageLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan PendingRefundDuration = TimeSpan.FromHours(1);

    public async Task<(string Token, DateTimeOffset ExpiresAt)> StartAsync(
        Guid connectionId, StartRequest request, SimulatorSettings settings, CancellationToken cancellationToken)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        var state = new PaymentState
        {
            Status = SimulatedPaymentStatus.AwaitingCard,
            Amount = request.Purpose == PaymentPurpose.Setup ? 0 : request.Amount.MinorUnits,
            Currency = request.Amount.Currency,
            Exponent = request.Amount.Exponent,
            Purpose = request.Purpose,
            SaveCard = request.SaveCard || request.Purpose != PaymentPurpose.Charge,
            AskToSave = request.Purpose == PaymentPurpose.Charge && request.SaveCard && settings.SavesCards == CardSaving.DuringPaymentWhenCustomerAgrees,
            ReturnUrl = request.ReturnUrl.AbsoluteUri,
            CancelUrl = request.CancelUrl.AbsoluteUri,
            ExpiresAt = now + PageLifetime,
        };

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            // Starting again before the customer paid gives the payment a new page.
            var existing = await ledger.FindAsync(connectionId, SimulatorEntryKind.Payment, request.Reference, cancellationToken);
            if (existing is not null && SimulatorJson.Read<PaymentState>(existing).Status is not (SimulatedPaymentStatus.AwaitingCard or SimulatedPaymentStatus.AwaitingAuthentication))
            {
                throw new InvalidOperationException($"The simulated payment {request.Reference} is already over; start a payment with a new reference.");
            }

            var entry = existing is null
                ? new SimulatorEntry(Guid.CreateVersion7(now), connectionId, SimulatorEntryKind.Payment, request.Reference, Hash(token), SimulatorJson.Write(state), 0, now)
                : existing with { TokenHash = Hash(token), State = SimulatorJson.Write(state) };
            if (await ledger.SaveAsync(entry, cancellationToken))
            {
                return (token, state.ExpiresAt!.Value);
            }
        }

        throw Contention(request.Reference);
    }

    public async Task<CardPage?> ReadPageAsync(string token, CancellationToken cancellationToken)
    {
        if (await FindPageAsync(token, cancellationToken) is not { } entry)
        {
            return null;
        }

        var state = SimulatorJson.Read<PaymentState>(entry);
        var step = state.Status switch
        {
            SimulatedPaymentStatus.AwaitingCard => CardPageStep.CardEntry,
            SimulatedPaymentStatus.AwaitingAuthentication => CardPageStep.Authentication,
            _ => CardPageStep.Finished,
        };
        return new CardPage(
            step, state.Amount, state.Currency, state.Exponent, state.Purpose, state.AskToSave, state.Card?[^4..], step == CardPageStep.Finished ? RedirectOf(state) : null);
    }

    /// <summary>Takes a card from the page. Numbers other than test cards are refused before anything is stored (SIM-4).</summary>
    public async Task<CardPageResult> SubmitCardAsync(string token, CardEntryForm form, CancellationToken cancellationToken)
    {
        if (TestCards.Find(form.Number) is not { } card)
        {
            return new CardPageResult.Invalid(CardEntryError.NotATestCard);
        }

        if (ReadExpiry(form.Expiry) is not var (month, year))
        {
            return new CardPageResult.Invalid(CardEntryError.InvalidExpiry);
        }

        if (form.Cvc is not { Length: 3 or 4 } cvc || !cvc.All(char.IsAsciiDigit))
        {
            return new CardPageResult.Invalid(CardEntryError.InvalidCvc);
        }

        return await UpdatePageAsync(token, SimulatedPaymentStatus.AwaitingCard, (entry, state) =>
        {
            state = state with { Card = card.Number, ExpMonth = month, ExpYear = year, SaveCard = state.SaveCard && (!state.AskToSave || form.SaveCard) };
            return card.ThreeDSecure
                ? Task.FromResult(state with { Status = SimulatedPaymentStatus.AwaitingAuthentication })
                : FinishAsync(entry, state, card, ThreeDSecureResult.NotAttempted, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>The simulated bank's answer to a 3D Secure challenge (SIM-5).</summary>
    public Task<CardPageResult> AuthenticateAsync(string token, AuthenticationDecision decision, CancellationToken cancellationToken) =>
        UpdatePageAsync(token, SimulatedPaymentStatus.AwaitingAuthentication, (entry, state) => decision switch
        {
            AuthenticationDecision.Approve => FinishAsync(entry, state, TestCards.Find(state.Card)!, ThreeDSecureResult.Authenticated, cancellationToken),
            AuthenticationDecision.Decline => Task.FromResult(state with
            {
                Status = SimulatedPaymentStatus.Failed,
                FailureCode = FailureCode.AuthenticationFailed,
                ThreeDSecure = ThreeDSecureResult.Failed,
            }),
            _ => Task.FromResult(state with { Status = SimulatedPaymentStatus.Abandoned, FailureCode = FailureCode.Abandoned }),
        }, cancellationToken);

    public Task<CardPageResult> CancelAsync(string token, CancellationToken cancellationToken) =>
        UpdatePageAsync(token, null, (_, state) =>
            Task.FromResult(state with { Status = SimulatedPaymentStatus.Abandoned, FailureCode = FailureCode.Abandoned, Cancelled = true }), cancellationToken);

    public async Task<PaymentOutcome> CompleteAsync(Guid connectionId, string reference, CancellationToken cancellationToken) =>
        await ledger.FindAsync(connectionId, SimulatorEntryKind.Payment, reference, cancellationToken) is { } entry
            ? Outcome(entry, Expire(SimulatorJson.Read<PaymentState>(entry)))
            : PaymentOutcome.Failure(FailureCode.NotSubmitted, providerMessage: "The simulator has no payment with this reference.");

    /// <summary>Charges a saved card without the customer, as the card's behavior says (SIM §3, SIM-8).</summary>
    public async Task<PaymentOutcome> ChargeAsync(Guid connectionId, ChargeRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            // Charges are idempotent by reference, as a provider that rejects a repeated order ID would be.
            if (await ledger.FindAsync(connectionId, SimulatorEntryKind.Payment, request.Reference, cancellationToken) is { } existing)
            {
                return Outcome(existing, SimulatorJson.Read<PaymentState>(existing));
            }

            if (await ledger.FindAsync(connectionId, SimulatorEntryKind.Card, request.SavedCard.Token, cancellationToken) is not { } cardEntry
                || SimulatorJson.Read<CardState>(cardEntry) is not { Removed: false } cardState)
            {
                return PaymentOutcome.Failure(FailureCode.PaymentMethodMissing, providerMessage: "The simulator has no such saved card.");
            }

            var card = TestCards.Find(cardState.Card)!;
            var behavior = CardBehavior.Parse(cardState.Behavior) ?? card.Saved;
            if (card.FailsAfterCharges is { } limit && cardState.Behavior is null && cardState.Charges >= limit)
            {
                behavior = CardBehavior.Fail(FailureCode.InsufficientFunds);
            }

            if (behavior.Kind == CardBehaviorKind.TimeoutBeforeCharge)
            {
                return PaymentOutcome.Unknown("The simulated charge timed out before it reached the provider.");
            }

            var now = clock.GetUtcNow();
            var charged = behavior.Kind is CardBehaviorKind.Succeed or CardBehaviorKind.TimeoutAfterCharge;
            var state = new PaymentState
            {
                Status = behavior.Kind switch
                {
                    CardBehaviorKind.Fail => SimulatedPaymentStatus.Failed,
                    CardBehaviorKind.Pending => SimulatedPaymentStatus.Pending,
                    _ => SimulatedPaymentStatus.Succeeded,
                },
                Amount = request.Amount.MinorUnits,
                Currency = request.Amount.Currency,
                Exponent = request.Amount.Exponent,
                Card = card.Number,
                ExpMonth = cardState.ExpMonth,
                ExpYear = cardState.ExpYear,
                FailureCode = behavior.FailureCode,
                SucceededAt = charged ? now : null,
            };
            var payment = new SimulatorEntry(Guid.CreateVersion7(now), connectionId, SimulatorEntryKind.Payment, request.Reference, null, SimulatorJson.Write(state), 0, now);
            if (charged && !await ledger.SaveAsync(cardEntry with { State = SimulatorJson.Write(cardState with { Charges = cardState.Charges + 1 }) }, cancellationToken))
            {
                continue;
            }

            if (!await ledger.SaveAsync(payment, cancellationToken))
            {
                continue;
            }

            if (state.Status != SimulatedPaymentStatus.Pending)
            {
                Notify(payment, charged ? "payment.succeeded" : "payment.failed");
            }

            return behavior.Kind == CardBehaviorKind.TimeoutAfterCharge
                ? PaymentOutcome.Unknown("The simulated charge was made, but its answer timed out.")
                : Outcome(payment, state);
        }

        throw Contention(request.Reference);
    }

    public async Task<PaymentLookup> FindPaymentAsync(Guid connectionId, string reference, CancellationToken cancellationToken) =>
        await ledger.FindAsync(connectionId, SimulatorEntryKind.Payment, reference, cancellationToken) is { } entry
            ? new PaymentLookup.Found(Outcome(entry, Expire(SimulatorJson.Read<PaymentState>(entry))))
            : new PaymentLookup.NotFound();

    public async Task<RefundOutcome> RefundAsync(Guid connectionId, RefundRequest request, SimulatorSettings settings, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await ledger.FindAsync(connectionId, SimulatorEntryKind.Refund, request.Reference, cancellationToken) is { } existing)
            {
                return RefundOutcomeOf(existing, SimulatorJson.Read<RefundState>(existing));
            }

            if (await ledger.FindAsync(connectionId, SimulatorEntryKind.Payment, request.PaymentReference, cancellationToken) is not { } paymentEntry
                || SimulatorJson.Read<PaymentState>(paymentEntry) is not { Status: SimulatedPaymentStatus.Succeeded } payment)
            {
                return Refused("payment_not_refundable", "The simulator has no succeeded payment with this reference.");
            }

            var now = clock.GetUtcNow();
            var refunded = payment.Refunded;
            var sameDay = payment.SucceededAt?.UtcDateTime.Date == now.UtcDateTime.Date;
            var full = refunded == 0 && request.Amount.MinorUnits == payment.Amount;
            if (refunded + request.Amount.MinorUnits > payment.Amount || request.Amount.MinorUnits <= 0)
            {
                return Refused("amount_too_large", "The refund is more than what is left of the payment.");
            }

            if (!full && (settings.Refunds == RefundSupport.Full || (settings.Refunds == RefundSupport.PartialAfterSettlementDay && sameDay)))
            {
                return Refused("partial_refund_not_allowed", "The simulator does not refund part of this payment now.");
            }

            var behavior = TestCards.Find(payment.Card)?.Refunds ?? RefundBehavior.Succeed;
            var refund = new RefundState
            {
                Status = behavior switch
                {
                    RefundBehavior.Fail => RefundStatus.Failed,
                    RefundBehavior.PendingForAnHour => RefundStatus.Pending,
                    _ => RefundStatus.Succeeded,
                },
                Kind = sameDay && full ? RefundKind.Void : RefundKind.Refund,
                Amount = request.Amount.MinorUnits,
                PaymentReference = request.PaymentReference,
                SettlesAt = behavior == RefundBehavior.PendingForAnHour ? now + PendingRefundDuration : null,
                FailureCode = behavior == RefundBehavior.Fail ? "refund_declined" : null,
            };
            if (refund.Status != RefundStatus.Failed
                && !await ledger.SaveAsync(paymentEntry with { State = SimulatorJson.Write(payment with { Refunded = refunded + refund.Amount }) }, cancellationToken))
            {
                continue;
            }

            var entry = new SimulatorEntry(Guid.CreateVersion7(now), connectionId, SimulatorEntryKind.Refund, request.Reference, null, SimulatorJson.Write(refund), 0, now);
            if (await ledger.SaveAsync(entry, cancellationToken))
            {
                if (refund.Status != RefundStatus.Pending)
                {
                    Notify(entry, refund.Status == RefundStatus.Succeeded ? "refund.succeeded" : "refund.failed");
                }

                return RefundOutcomeOf(entry, refund);
            }
        }

        throw Contention(request.Reference);
    }

    /// <summary>Finds a refund; a pending refund settles once the billing clock passes its time.</summary>
    public async Task<RefundLookup> FindRefundAsync(Guid connectionId, string reference, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await ledger.FindAsync(connectionId, SimulatorEntryKind.Refund, reference, cancellationToken) is not { } entry)
            {
                return new RefundLookup.NotFound();
            }

            var refund = SimulatorJson.Read<RefundState>(entry);
            if (refund is not { Status: RefundStatus.Pending, SettlesAt: { } settlesAt } || clock.GetUtcNow() < settlesAt)
            {
                return new RefundLookup.Found(RefundOutcomeOf(entry, refund));
            }

            var settled = refund with { Status = RefundStatus.Succeeded };
            if (await ledger.SaveAsync(entry with { State = SimulatorJson.Write(settled) }, cancellationToken))
            {
                Notify(entry, "refund.succeeded");
                return new RefundLookup.Found(RefundOutcomeOf(entry, settled));
            }
        }

        throw Contention(reference);
    }

    public async Task<bool> RemoveCardAsync(Guid connectionId, string token, CancellationToken cancellationToken) =>
        await UpdateCardAsync(connectionId, token, card => card with { Removed = true }, cancellationToken);

    /// <summary>Changes how a saved card answers from now on (SIM-8).</summary>
    public Task<bool> SetCardBehaviorAsync(Guid connectionId, string token, CardBehavior behavior, CancellationToken cancellationToken) =>
        UpdateCardAsync(connectionId, token, card => card with { Behavior = behavior.ToString() }, cancellationToken);

    private async Task<bool> UpdateCardAsync(Guid connectionId, string token, Func<CardState, CardState> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await ledger.FindAsync(connectionId, SimulatorEntryKind.Card, token, cancellationToken) is not { } entry)
            {
                return false;
            }

            if (await ledger.SaveAsync(entry with { State = SimulatorJson.Write(change(SimulatorJson.Read<CardState>(entry))) }, cancellationToken))
            {
                return true;
            }
        }

        throw Contention(token);
    }

    /// <summary>The signature of a notification body: <c>t={unix seconds},v1={hex HMAC-SHA256 of "t.body"}</c>.</summary>
    public async Task<string> SignAsync(Guid connectionId, long timestamp, byte[] body, CancellationToken cancellationToken)
    {
        var key = await SigningKeyAsync(connectionId, cancellationToken);
        return $"t={timestamp},v1={Convert.ToHexStringLower(Hmac.Compute(HmacAlgorithm.Sha256, key, Signed(timestamp, body)))}";
    }

    /// <summary>Checks a notification's signature with the connection's key, in constant time.</summary>
    public async Task<bool> VerifyAsync(Guid connectionId, string? signature, byte[] body, CancellationToken cancellationToken)
    {
        var parts = signature?.Split(',').Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1]);
        if (parts is null || !parts.TryGetValue("t", out var t) || !long.TryParse(t, out var timestamp) || !parts.TryGetValue("v1", out var received))
        {
            return false;
        }

        var key = await SigningKeyAsync(connectionId, cancellationToken);
        return Hmac.MatchesHex(Hmac.Compute(HmacAlgorithm.Sha256, key, Signed(timestamp, body)), received);
    }

    private async Task<byte[]> SigningKeyAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await ledger.FindAsync(connectionId, SimulatorEntryKind.Key, "signing", cancellationToken) is { } stored)
            {
                return Convert.FromBase64String(SimulatorJson.Read<KeyState>(stored).Key);
            }

            var now = clock.GetUtcNow();
            var state = new KeyState(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            await ledger.SaveAsync(new SimulatorEntry(Guid.CreateVersion7(now), connectionId, SimulatorEntryKind.Key, "signing", null, SimulatorJson.Write(state), 0, now), cancellationToken);
        }

        throw Contention("signing");
    }

    private static byte[] Signed(long timestamp, byte[] body) => [.. Encoding.UTF8.GetBytes($"{timestamp}."), .. body];

    private void Notify(SimulatorEntry entry, string type) => notifier?.Notify(entry.ConnectionId, type, entry.Reference);

    private async Task<SimulatorEntry?> FindPageAsync(string token, CancellationToken cancellationToken) =>
        await ledger.FindByTokenAsync(Hash(token), cancellationToken) is { Kind: SimulatorEntryKind.Payment } entry
        && SimulatorJson.Read<PaymentState>(entry).ExpiresAt is { } expiresAt
        && clock.GetUtcNow() < expiresAt
            ? entry
            : null;

    /// <summary>Moves a page's payment on from <paramref name="from"/> (any open step when <see langword="null"/>); a payment that is over sends the customer back.</summary>
    private async Task<CardPageResult> UpdatePageAsync(
        string token, SimulatedPaymentStatus? from, Func<SimulatorEntry, PaymentState, Task<PaymentState>> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await FindPageAsync(token, cancellationToken) is not { } entry)
            {
                return new CardPageResult.NotFound();
            }

            var state = SimulatorJson.Read<PaymentState>(entry);
            var open = state.Status is SimulatedPaymentStatus.AwaitingCard or SimulatedPaymentStatus.AwaitingAuthentication;
            if (!open || (from is { } expected && state.Status != expected))
            {
                return open ? new CardPageResult.NotFound() : new CardPageResult.Redirect(RedirectOf(state));
            }

            var next = await change(entry, state);
            if (await ledger.SaveAsync(entry with { State = SimulatorJson.Write(next) }, cancellationToken))
            {
                if (next.Status != SimulatedPaymentStatus.AwaitingAuthentication)
                {
                    Notify(entry, next.Status == SimulatedPaymentStatus.Succeeded ? "payment.succeeded" : "payment.failed");
                }

                return next.Status == SimulatedPaymentStatus.AwaitingAuthentication ? new CardPageResult.Authenticate() : new CardPageResult.Redirect(RedirectOf(next));
            }
        }

        return new CardPageResult.NotFound();
    }

    /// <summary>Ends a payment with the customer present as the card says, saving the card when it succeeds and should be saved.</summary>
    private async Task<PaymentState> FinishAsync(SimulatorEntry entry, PaymentState state, TestCard card, ThreeDSecureResult threeDSecure, CancellationToken cancellationToken)
    {
        if (card.Present.Kind == CardBehaviorKind.Fail)
        {
            return state with { Status = SimulatedPaymentStatus.Failed, FailureCode = card.Present.FailureCode, ThreeDSecure = threeDSecure };
        }

        var now = clock.GetUtcNow();
        string? savedCard = null;
        if (state.SaveCard)
        {
            savedCard = $"sim_card_{Guid.CreateVersion7(now):N}";
            var cardState = new CardState { Card = card.Number, ExpMonth = state.ExpMonth ?? 12, ExpYear = state.ExpYear ?? now.Year };
            await ledger.SaveAsync(
                new SimulatorEntry(Guid.CreateVersion7(now), entry.ConnectionId, SimulatorEntryKind.Card, savedCard, null, SimulatorJson.Write(cardState), 0, now),
                cancellationToken);
        }

        return state with { Status = SimulatedPaymentStatus.Succeeded, ThreeDSecure = threeDSecure, SavedCardToken = savedCard, SucceededAt = now };
    }

    private static PaymentOutcome Outcome(SimulatorEntry entry, PaymentState state)
    {
        var card = TestCards.Find(state.Card);
        return new PaymentOutcome
        {
            Status = state.Status switch
            {
                SimulatedPaymentStatus.Succeeded => PaymentStatus.Succeeded,
                SimulatedPaymentStatus.Failed or SimulatedPaymentStatus.Abandoned => PaymentStatus.Failed,
                _ => PaymentStatus.Pending,
            },
            ProviderPaymentId = $"sim_pay_{entry.Id:N}",
            ChargedAmount = state.Status == SimulatedPaymentStatus.Succeeded ? new Amount(state.Amount, state.Currency, state.Exponent) : null,
            FailureCode = state.FailureCode,
            ProviderFailureCode = state.FailureCode is { } code ? CardBehavior.Fail(code).ToString() : null,
            ThreeDSecure = state.ThreeDSecure,
            SavedCard = state.SavedCardToken is { } token && card is not null
                ? new SavedCard
                {
                    Token = token,
                    Brand = card.Brand,
                    Funding = card.Funding,
                    Bin = card.Bin,
                    Last4 = card.Last4,
                    ExpMonth = state.ExpMonth,
                    ExpYear = state.ExpYear,
                    Issuer = "Simulator Bank",
                    Country = "TR",
                }
                : null,
        };
    }

    private static RefundOutcome RefundOutcomeOf(SimulatorEntry entry, RefundState refund) => new()
    {
        Status = refund.Status,
        Kind = refund.Kind,
        ProviderRefundId = $"sim_ref_{entry.Id:N}",
        FailureCode = refund.FailureCode,
        FailureMessage = refund.FailureCode is null ? null : "The simulated bank declined the refund.",
    };

    private static RefundOutcome Refused(string code, string message) => new() { Status = RefundStatus.Failed, FailureCode = code, FailureMessage = message };

    private static Uri RedirectOf(PaymentState state) => new(state.Cancelled ? state.CancelUrl! : state.ReturnUrl!);

    /// <summary>A page the customer never finished is abandoned once it expires, as at a real provider.</summary>
    private PaymentState Expire(PaymentState state) =>
        state.Status is SimulatedPaymentStatus.AwaitingCard or SimulatedPaymentStatus.AwaitingAuthentication && state.ExpiresAt <= clock.GetUtcNow()
            ? state with { Status = SimulatedPaymentStatus.Abandoned, FailureCode = FailureCode.Abandoned }
            : state;

    private (int Month, int Year)? ReadExpiry(string? expiry)
    {
        var parts = expiry?.Split('/', StringSplitOptions.TrimEntries);
        if (parts is not [var monthText, var yearText]
            || !int.TryParse(monthText, out var month) || month is < 1 or > 12
            || !int.TryParse(yearText, out var year) || year is < 0 or > 99)
        {
            return null;
        }

        year += 2000;
        var now = clock.GetUtcNow().UtcDateTime;
        return year > now.Year || (year == now.Year && month >= now.Month) ? (month, year) : null;
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static InvalidOperationException Contention(string reference) =>
        new($"The simulated record {reference} kept changing while it was updated.");
}
