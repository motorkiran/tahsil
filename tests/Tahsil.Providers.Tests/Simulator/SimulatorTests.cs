using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Simulator;

namespace Tahsil.Providers.Tests.Simulator;

/// <summary>Every test card of SIM §3, through the adapter contract.</summary>
public sealed partial class SimulatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private readonly InMemoryLedger _ledger = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly SimulatorBank _bank;
    private readonly SimulatorProvider _simulator;
    private readonly Guid _connection = Guid.CreateVersion7();

    public SimulatorTests()
    {
        _bank = new SimulatorBank(_ledger, _clock);
        _simulator = new SimulatorProvider(_bank, new Uri("https://billing.example.com/"));
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Requirement", "SIM-3")]
    [InlineData("4242 4242 4242 4242", null)]
    [InlineData("4000 0000 0000 3220", null)]
    [InlineData("4000 0025 0000 3155", null)]
    [InlineData("4000 0000 0000 0341", null)]
    [InlineData("4000 0000 0000 9995", FailureCode.InsufficientFunds)]
    [InlineData("4000 0000 0000 0002", FailureCode.CardDeclined)]
    [InlineData("4000 0000 0000 0069", FailureCode.ExpiredCard)]
    [InlineData("4000 0000 0000 9987", FailureCode.LostOrStolen)]
    [InlineData("4000 0000 0000 0259", null)]
    [InlineData("4000 0000 0000 0119", FailureCode.ProcessingError)]
    [InlineData("4000 0000 0000 7726", null)]
    [InlineData("4000 0000 0000 5126", null)]
    [InlineData("4000 0000 0000 1000", null)]
    [InlineData("4000 0000 0000 1018", null)]
    [InlineData("4000 0000 0000 1026", null)]
    [InlineData("9792 0000 0000 0001", null)]
    public async Task WithTheCustomerPresent_EachTestCardAnswersAsListed(string number, FailureCode? failure)
    {
        var outcome = await PayAsync(number, saveCard: true);

        outcome.Status.ShouldBe(failure is null ? PaymentStatus.Succeeded : PaymentStatus.Failed);
        outcome.FailureCode.ShouldBe(failure);
        if (failure is null)
        {
            outcome.ChargedAmount.ShouldBe(new Amount(12_500, "TRY", 2));
            outcome.SavedCard.ShouldNotBeNull().Last4.ShouldBe(number[^4..]);
            outcome.ThreeDSecure.ShouldBe(number.StartsWith("4000 0000 0000 3220", StringComparison.Ordinal) || number.StartsWith("4000 0025", StringComparison.Ordinal)
                ? ThreeDSecureResult.Authenticated
                : ThreeDSecureResult.NotAttempted);
        }
        else
        {
            outcome.SavedCard.ShouldBeNull();
        }
    }

    [Theory]
    [Trait("Requirement", "SIM-3")]
    [InlineData("4242424242424242", PaymentStatus.Succeeded, null)]
    [InlineData("4000000000003220", PaymentStatus.Succeeded, null)]
    [InlineData("4000002500003155", PaymentStatus.Failed, FailureCode.AuthenticationRequired)]
    [InlineData("4000000000000341", PaymentStatus.Failed, FailureCode.CardDeclined)]
    [InlineData("4000000000000259", PaymentStatus.Failed, FailureCode.FraudSuspected)]
    [InlineData("4000000000007726", PaymentStatus.Succeeded, null)]
    [InlineData("4000000000005126", PaymentStatus.Succeeded, null)]
    [InlineData("4000000000001000", PaymentStatus.Unknown, null)]
    [InlineData("4000000000001018", PaymentStatus.Unknown, null)]
    [InlineData("9792000000000001", PaymentStatus.Succeeded, null)]
    public async Task ChargedLater_EachSavedCardAnswersAsListed(string number, PaymentStatus status, FailureCode? failure)
    {
        var card = (await PayAsync(number, saveCard: true)).SavedCard!;

        var outcome = await ChargeAsync(card, "renewal-1");

        outcome.Status.ShouldBe(status);
        outcome.FailureCode.ShouldBe(failure);
    }

    [Theory]
    [Trait("Requirement", "SIM-3")]
    [Trait("Requirement", "ADP-4")]
    [InlineData("4000000000001000", true)]
    [InlineData("4000000000001018", false)]
    public async Task TimedOutCharges_AreFoundByReconciliation_OnlyWhenTheyWereMade(string number, bool charged)
    {
        var card = (await PayAsync(number, saveCard: true)).SavedCard!;
        (await ChargeAsync(card, "renewal-1")).Status.ShouldBe(PaymentStatus.Unknown);

        var lookup = await _simulator.FindPaymentAsync(Context(), new PaymentQuery("renewal-1", null, Start), CancellationToken);

        if (charged)
        {
            lookup.ShouldBeOfType<PaymentLookup.Found>().Outcome.Status.ShouldBe(PaymentStatus.Succeeded);
        }
        else
        {
            lookup.ShouldBeOfType<PaymentLookup.NotFound>();
        }
    }

    [Fact]
    [Trait("Requirement", "SIM-3")]
    public async Task OneCard_SucceedsThreeRenewals_ThenRunsOutOfFunds()
    {
        var card = (await PayAsync("4000000000001026", saveCard: true)).SavedCard!;

        var outcomes = new List<PaymentOutcome>();
        for (var renewal = 1; renewal <= 4; renewal++)
        {
            outcomes.Add(await ChargeAsync(card, $"renewal-{renewal}"));
        }

        outcomes.Select(outcome => outcome.Status).ShouldBe([PaymentStatus.Succeeded, PaymentStatus.Succeeded, PaymentStatus.Succeeded, PaymentStatus.Failed]);
        outcomes[3].FailureCode.ShouldBe(FailureCode.InsufficientFunds);
    }

    [Fact]
    [Trait("Requirement", "SIM-3")]
    public async Task Charges_AreIdempotentByReference()
    {
        var card = (await PayAsync("4000000000001026", saveCard: true)).SavedCard!;

        var first = await ChargeAsync(card, "renewal-1");
        var again = await ChargeAsync(card, "renewal-1");

        again.ProviderPaymentId.ShouldBe(first.ProviderPaymentId);
        _ledger.Entries.Count(entry => entry.Kind == SimulatorEntryKind.Payment).ShouldBe(2);
    }

    [Theory]
    [Trait("Requirement", "SIM-5")]
    [InlineData(AuthenticationDecision.Decline, FailureCode.AuthenticationFailed)]
    [InlineData(AuthenticationDecision.DoNothing, FailureCode.Abandoned)]
    public async Task TheBankPage_CanDeclineOrBeLeft(AuthenticationDecision decision, FailureCode failure)
    {
        var token = await StartAsync("pay-1");
        (await _bank.SubmitCardAsync(token, Form("4000000000003220"), CancellationToken)).ShouldBeOfType<CardPageResult.Authenticate>();
        (await _bank.ReadPageAsync(token, CancellationToken))!.Step.ShouldBe(CardPageStep.Authentication);

        var result = await _bank.AuthenticateAsync(token, decision, CancellationToken);
        var outcome = await CompleteAsync("pay-1");

        result.ShouldBeOfType<CardPageResult.Redirect>().Url.ShouldBe(new Uri("https://shop.example.com/return"));
        (outcome.Status, outcome.FailureCode).ShouldBe((PaymentStatus.Failed, failure));
    }

    [Fact]
    [Trait("Requirement", "SIM-6")]
    public async Task CancellingOrNeverPaying_Abandons_AndCancelGoesToTheCancelUrl()
    {
        var cancelled = await StartAsync("pay-1");
        await StartAsync("pay-2");

        var result = await _bank.CancelAsync(cancelled, CancellationToken);
        var open = await CompleteAsync("pay-2");
        _clock.Advance(TimeSpan.FromHours(1));
        var expired = await CompleteAsync("pay-2");

        result.ShouldBeOfType<CardPageResult.Redirect>().Url.ShouldBe(new Uri("https://shop.example.com/cancel"));
        (await CompleteAsync("pay-1")).FailureCode.ShouldBe(FailureCode.Abandoned);
        open.Status.ShouldBe(PaymentStatus.Pending);
        (expired.Status, expired.FailureCode).ShouldBe((PaymentStatus.Failed, FailureCode.Abandoned));
    }

    [Theory]
    [Trait("Requirement", "SIM-4")]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("5555555555554444")]
    [InlineData("not a number")]
    [InlineData("")]
    public async Task OtherCardNumbers_AreRefused_WithoutBeingStored(string number)
    {
        var token = await StartAsync("pay-1");

        var result = await _bank.SubmitCardAsync(token, Form(number), CancellationToken);

        result.ShouldBe(new CardPageResult.Invalid(CardEntryError.NotATestCard));
        var digits = Regex.Replace(number, "[^0-9]", string.Empty);
        _ledger.Entries.ShouldAllBe(entry => digits.Length == 0 || !entry.State.Contains(digits, StringComparison.Ordinal));
        (await _bank.ReadPageAsync(token, CancellationToken))!.Step.ShouldBe(CardPageStep.CardEntry);
    }

    [Theory]
    [InlineData("09/26", "123", CardEntryError.InvalidExpiry)]
    [InlineData("13/30", "123", CardEntryError.InvalidExpiry)]
    [InlineData("12/30", "12", CardEntryError.InvalidCvc)]
    [InlineData("12/30", "12a4", CardEntryError.InvalidCvc)]
    public async Task ExpiredCardsAndBadCodes_AreRefused(string expiry, string cvc, CardEntryError error)
    {
        var token = await StartAsync("pay-1");

        (await _bank.SubmitCardAsync(token, new CardEntryForm("4242424242424242", expiry, cvc, true), CancellationToken))
            .ShouldBe(new CardPageResult.Invalid(error));
    }

    [Fact]
    [Trait("Requirement", "SIM-3")]
    public async Task Refunds_SettleOrFail_AsTheCardSays()
    {
        var pending = await PayAsync("4000000000007726", reference: "pay-pending");
        var failing = await PayAsync("4000000000005126", reference: "pay-failing");
        _clock.Advance(TimeSpan.FromDays(1));

        var refund = await RefundAsync("pay-pending", "refund-1", 5_000);
        var declined = await RefundAsync("pay-failing", "refund-2", 5_000);
        var before = await _simulator.FindRefundAsync(Context(), new RefundQuery("refund-1", "pay-pending", null, null, Start), CancellationToken);
        _clock.Advance(TimeSpan.FromHours(1));
        var after = await _simulator.FindRefundAsync(Context(), new RefundQuery("refund-1", "pay-pending", null, null, Start), CancellationToken);

        (pending.Status, failing.Status).ShouldBe((PaymentStatus.Succeeded, PaymentStatus.Succeeded));
        (refund.Status, refund.Kind).ShouldBe((RefundStatus.Pending, RefundKind.Refund));
        declined.Status.ShouldBe(RefundStatus.Failed);
        before.ShouldBeOfType<RefundLookup.Found>().Outcome.Status.ShouldBe(RefundStatus.Pending);
        after.ShouldBeOfType<RefundLookup.Found>().Outcome.Status.ShouldBe(RefundStatus.Succeeded);
    }

    [Fact]
    [Trait("Requirement", "SIM-2")]
    public async Task Refunds_VoidOnTheSameDay_AndNeverExceedThePayment()
    {
        await PayAsync("4242424242424242", reference: "pay-1");
        await PayAsync("4242424242424242", reference: "pay-2");

        var voided = await RefundAsync("pay-1", "refund-1", 12_500);
        var partial = await RefundAsync("pay-2", "refund-2", 10_000);
        var tooMuch = await RefundAsync("pay-2", "refund-3", 2_501);
        var rest = await RefundAsync("pay-2", "refund-4", 2_500);
        var repeated = await RefundAsync("pay-2", "refund-4", 2_500);

        (voided.Status, voided.Kind).ShouldBe((RefundStatus.Succeeded, RefundKind.Void));
        (partial.Status, partial.Kind).ShouldBe((RefundStatus.Succeeded, RefundKind.Refund));
        (tooMuch.Status, tooMuch.FailureCode).ShouldBe((RefundStatus.Failed, "amount_too_large"));
        rest.Status.ShouldBe(RefundStatus.Succeeded);
        repeated.ProviderRefundId.ShouldBe(rest.ProviderRefundId);
    }

    [Fact]
    [Trait("Requirement", "SIM-2")]
    public async Task Settings_RestrictTheCapabilities_ToMimicARealProvider()
    {
        var settings = new Dictionary<string, string> { ["setup"] = "verification_charge", ["refunds"] = "partial_after_settlement_day" };
        await PayAsync("4242424242424242", reference: "pay-1");

        var capabilities = _simulator.CapabilitiesFor(settings);
        var sameDayPartial = await _simulator.RefundAsync(Context(settings), Refund("pay-1", "refund-1", 100), CancellationToken);
        var setup = () => _simulator.StartAsync(Context(settings), Request("pay-2", PaymentPurpose.Setup), CancellationToken);

        (capabilities.Setup, capabilities.Refunds).ShouldBe((CardSetup.VerificationCharge, RefundSupport.PartialAfterSettlementDay));
        _simulator.CapabilitiesFor(new Dictionary<string, string>()).ShouldBe(_simulator.Descriptor.Capabilities);
        (sameDayPartial.Status, sameDayPartial.FailureCode).ShouldBe((RefundStatus.Failed, "partial_refund_not_allowed"));
        await Should.ThrowAsync<InvalidOperationException>(setup);
    }

    [Fact]
    [Trait("Requirement", "SIM-3")]
    public async Task CardsAreSavedOnlyWhenTheCustomerAgrees_WhereThePageAsks()
    {
        var settings = new Dictionary<string, string> { ["savesCards"] = "during_payment_when_customer_agrees" };

        var declined = await PayAsync("4242424242424242", saveCard: true, consent: false, reference: "pay-1", settings: settings);
        var agreed = await PayAsync("4242424242424242", saveCard: true, consent: true, reference: "pay-2", settings: settings);

        declined.SavedCard.ShouldBeNull();
        agreed.SavedCard.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Requirement", "SIM-3")]
    public async Task Setup_SavesTheCard_WithoutCharging()
    {
        var token = await StartAsync("setup-1", PaymentPurpose.Setup);
        await _bank.SubmitCardAsync(token, Form("9792000000000001"), CancellationToken);

        var outcome = await CompleteAsync("setup-1");

        outcome.Status.ShouldBe(PaymentStatus.Succeeded);
        outcome.ChargedAmount.ShouldBe(new Amount(0, "TRY", 2));
        outcome.SavedCard.ShouldNotBeNull();
        (outcome.SavedCard.Brand, outcome.SavedCard.Funding, outcome.SavedCard.Bin, outcome.SavedCard.ExpYear).ShouldBe(("troy", "debit", "979200", 2030));
    }

    [Fact]
    [Trait("Requirement", "SIM-8")]
    public async Task ASavedCardsBehavior_CanChange_AndRemovedCardsCannotBeCharged()
    {
        var card = (await PayAsync("4242424242424242", saveCard: true)).SavedCard!;

        (await _bank.SetCardBehaviorAsync(_connection, card.Token, CardBehavior.Parse("insufficient_funds")!.Value, CancellationToken)).ShouldBeTrue();
        var declined = await ChargeAsync(card, "renewal-1");
        (await _simulator.RemoveSavedCardAsync(Context(), new SavedCardReference(null, card.Token), CancellationToken)).Removed.ShouldBeTrue();
        var removed = await ChargeAsync(card, "renewal-2");

        declined.FailureCode.ShouldBe(FailureCode.InsufficientFunds);
        removed.FailureCode.ShouldBe(FailureCode.PaymentMethodMissing);
        CardBehavior.Parse("timeout_after_charge").ShouldBe(new CardBehavior(CardBehaviorKind.TimeoutAfterCharge));
        CardBehavior.Parse("nonsense").ShouldBeNull();
        CardBehavior.Fail(FailureCode.LostOrStolen).ToString().ShouldBe("lost_or_stolen");
    }

    [Fact]
    [Trait("Requirement", "SIM-3")]
    public async Task ThePage_IsEmbeddedFromTahsil_AndItsTokenIsStoredOnlyAsAHash()
    {
        var started = await _simulator.StartAsync(Context(), Request("pay-1"), CancellationToken);

        var html = started.Presentation.ShouldBeOfType<Presentation.Embed>().Html;
        var token = TokenOf(started);
        html.ShouldStartWith("<iframe class=\"provider-frame\" src=\"https://billing.example.com/simulator/pay/");
        token.Length.ShouldBe(43);
        _ledger.Entries.ShouldAllBe(entry => !entry.State.Contains(token, StringComparison.Ordinal) && !Encoding.UTF8.GetString(entry.TokenHash!).Contains(token, StringComparison.Ordinal));
        started.ExpiresAt.ShouldBe(Start.AddHours(1));
    }

    private async Task<PaymentOutcome> PayAsync(
        string number, bool saveCard = false, bool consent = true, string reference = "pay-1", IReadOnlyDictionary<string, string>? settings = null)
    {
        var token = await StartAsync(reference, saveCard: saveCard, settings: settings);
        var result = await _bank.SubmitCardAsync(token, Form(number, consent), CancellationToken);
        if (result is CardPageResult.Authenticate)
        {
            result = await _bank.AuthenticateAsync(token, AuthenticationDecision.Approve, CancellationToken);
        }

        result.ShouldBeOfType<CardPageResult.Redirect>().Url.ShouldBe(new Uri("https://shop.example.com/return"));
        return await CompleteAsync(reference);
    }

    private async Task<string> StartAsync(
        string reference, PaymentPurpose purpose = PaymentPurpose.Charge, bool saveCard = false, IReadOnlyDictionary<string, string>? settings = null) =>
        TokenOf(await _simulator.StartAsync(Context(settings), Request(reference, purpose) with { SaveCard = saveCard }, CancellationToken));

    private Task<PaymentOutcome> CompleteAsync(string reference) =>
        _simulator.CompleteAsync(Context(), new CompleteRequest { PaymentId = Guid.CreateVersion7(), Reference = reference, Amount = new Amount(12_500, "TRY", 2) }, CancellationToken);

    private Task<PaymentOutcome> ChargeAsync(SavedCard card, string reference) =>
        _simulator.ChargeSavedCardAsync(Context(), new ChargeRequest
        {
            PaymentId = Guid.CreateVersion7(),
            Reference = reference,
            Amount = new Amount(9_900, "TRY", 2),
            SavedCard = new SavedCardReference(card.ProviderCustomer, card.Token),
            Customer = new CustomerInfo { Id = Guid.CreateVersion7() },
        }, CancellationToken);

    private Task<RefundOutcome> RefundAsync(string payment, string reference, long amount) =>
        _simulator.RefundAsync(Context(), Refund(payment, reference, amount), CancellationToken);

    private static RefundRequest Refund(string payment, string reference, long amount) => new()
    {
        RefundId = Guid.CreateVersion7(),
        Reference = reference,
        PaymentReference = payment,
        PaymentAmount = new Amount(12_500, "TRY", 2),
        PaymentSucceededAt = Start,
        Amount = new Amount(amount, "TRY", 2),
    };

    private static StartRequest Request(string reference, PaymentPurpose purpose = PaymentPurpose.Charge) => new()
    {
        PaymentId = Guid.CreateVersion7(),
        Reference = reference,
        Amount = new Amount(12_500, "TRY", 2),
        Purpose = purpose,
        Customer = new CustomerInfo { Id = Guid.CreateVersion7(), Email = "ayse@example.com" },
        ReturnUrl = new Uri("https://shop.example.com/return"),
        CancelUrl = new Uri("https://shop.example.com/cancel"),
    };

    private static CardEntryForm Form(string number, bool saveCard = true) => new(number, "12/30", "123", saveCard);

    private static string TokenOf(StartResult started) =>
        TokenPattern().Match(started.Presentation.ShouldBeOfType<Presentation.Embed>().Html).Groups["token"].Value;

    private ProviderContext Context(IReadOnlyDictionary<string, string>? settings = null) => new()
    {
        ConnectionId = _connection,
        Environment = ProviderEnvironment.Sandbox,
        Credentials = new Dictionary<string, string>(),
        Settings = settings ?? new Dictionary<string, string>(),
        Http = new HttpClient(),
        Time = TimeProvider.System,
        Logger = NullLogger.Instance,
        NotificationUrl = new Uri($"https://billing.example.com/provider-notifications/{_connection}"),
    };

    [GeneratedRegex("simulator/pay/(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();
}
