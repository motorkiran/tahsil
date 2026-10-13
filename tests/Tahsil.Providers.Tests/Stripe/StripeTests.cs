using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Stripe;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Stripe;
using Tahsil.Providers.Tests.Recorded;
using PaymentStatus = Tahsil.Providers.Abstractions.PaymentStatus;

namespace Tahsil.Providers.Tests.Stripe;

/// <summary>The Stripe adapter against its signature vector and exchanges in Stripe's API shapes (docs/specs/providers/stripe.md).</summary>
public sealed class StripeTests
{
    private const string Reference = "0199c0de0000700080000000000000a1";
    private const string RenewalReference = "0199c0de0000700080000000000000a3";
    private const string RefundReference = "0199c0de0000700080000000000000b1";
    private const string Secret = "whsec_test_secret";
    private const string Card = """{"paymentMethodId":"pm_1Visa"}""";

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Amount Amount = new(2_990, "USD", 2);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly StripeProvider _stripe = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "STR-11")]
    public void Signatures_FollowTheVector_AsStripeNetChecksThem()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"v1.payment_intent.succeeded"}""");
        const string Vector = "f0c6679f9a13a2b276a0e2b55a370225f655545e40ff2781a00a2e86cf2a1134";
        var at = DateTimeOffset.FromUnixTimeSeconds(1_791_331_200);

        StripeSignature.IsValid($"t=1791331200,v0=ignored,v1={Vector}", body, Secret, at.AddMinutes(4)).ShouldBeTrue();
        StripeSignature.IsValid($"t=1791331200,v1=deadbeef,v1={Vector}", body, Secret, at).ShouldBeTrue("any of the signatures may match");
        StripeSignature.IsValid($"t=1791331200,v1={Vector}", body, Secret, at.AddMinutes(6)).ShouldBeFalse("older than five minutes");
        StripeSignature.IsValid($"t=1791331200,v1={Vector}", body, "whsec_other", at).ShouldBeFalse();
        StripeSignature.IsValid($"t=1791331200,v0={Vector}", body, Secret, at).ShouldBeFalse("v0 is never trusted");
        Should.NotThrow(() => EventUtility.ValidateSignature(Encoding.UTF8.GetString(body), $"t=1791331200,v1={Vector}", Secret, 1_791_331_200, 300));
    }

    [Theory]
    [Trait("Requirement", "STR-8")]
    [InlineData("card_error", "card_declined", "insufficient_funds", FailureCode.InsufficientFunds)]
    [InlineData("card_error", "card_declined", "card_velocity_exceeded", FailureCode.LimitExceeded)]
    [InlineData("card_error", "expired_card", null, FailureCode.ExpiredCard)]
    [InlineData("card_error", "card_declined", "stolen_card", FailureCode.LostOrStolen)]
    [InlineData("card_error", "card_declined", "fraudulent", FailureCode.FraudSuspected)]
    [InlineData("card_error", "card_declined", "currency_not_supported", FailureCode.CardNotSupported)]
    [InlineData("card_error", "authentication_required", "authentication_required", FailureCode.AuthenticationRequired)]
    [InlineData("invalid_request_error", "payment_intent_authentication_failure", null, FailureCode.AuthenticationFailed)]
    [InlineData("card_error", "card_declined", "stop_payment_order", FailureCode.AuthorizationRevoked)]
    [InlineData("card_error", "card_declined", "do_not_honor", FailureCode.CardDeclined)]
    [InlineData("card_error", "card_declined", "something_new", FailureCode.CardDeclined)]
    [InlineData("card_error", "processing_error", null, FailureCode.ProcessingError)]
    [InlineData("invalid_request_error", "lock_timeout", null, FailureCode.ProviderUnavailable)]
    [InlineData("invalid_request_error", "livemode_mismatch", null, FailureCode.InvalidRequest)]
    [InlineData("invalid_request_error", "parameter_missing", null, FailureCode.InvalidRequest)]
    [InlineData("api_error", null, null, FailureCode.ProviderError)]
    public void Failures_MapByDeclineCodeThenCodeThenType(string type, string? code, string? declineCode, FailureCode failure) =>
        StripeFailures.Map(new StripeError { Type = type, Code = code, DeclineCode = declineCode }, HttpStatusCode.PaymentRequired).ShouldBe(failure);

    [Fact]
    [Trait("Requirement", "STR-4")]
    [Trait("Requirement", "STR-5")]
    public async Task APayment_CreatesTheCustomer_AndOneLineCheckoutSession()
    {
        var handler = new RecordedExchangeHandler("stripe/start-payment.json");

        var started = await _stripe.StartAsync(Context(handler, new Dictionary<string, string> { ["statementDescriptorSuffix"] = "PRO" }), new StartRequest
        {
            PaymentId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a1"),
            Reference = Reference,
            Amount = Amount,
            Purpose = PaymentPurpose.Charge,
            SaveCard = true,
            Customer = Customer(),
            Description = "Pro (monthly)",
            ReturnUrl = new Uri("https://billing.example.com/checkout/cs_x/return"),
            CancelUrl = new Uri("https://billing.example.com/checkout/cs_x"),
            Locale = "en",
        }, CancellationToken);

        handler.AssertComplete();
        started.Presentation.ShouldBe(new Presentation.Redirect(new Uri("https://checkout.stripe.com/c/pay/cs_test_a1")));
        (started.ProviderCustomer, started.ProviderData["checkoutSessionId"]).ShouldBe(("cus_TaHs1L", "cs_test_a1"));
        started.ExpiresAt.ShouldBe(Now.AddMinutes(31));
    }

    [Fact]
    [Trait("Requirement", "STR-6")]
    [Trait("Requirement", "STR-7")]
    public async Task APaidSession_Succeeds_WithTheCardItSaved()
    {
        var handler = new RecordedExchangeHandler("stripe/session-paid.json");

        var outcome = await _stripe.CompleteAsync(Context(handler), Complete("cs_test_a1"), CancellationToken);

        handler.AssertComplete();
        (outcome.Status, outcome.ProviderPaymentId, outcome.ChargedAmount).ShouldBe((PaymentStatus.Succeeded, "pi_3Tahsil", Amount));
        outcome.SavedCard.ShouldBe(new SavedCard
        {
            ProviderCustomer = "cus_TaHs1L",
            Token = Card,
            Brand = "visa",
            Funding = "credit",
            Last4 = "4242",
            ExpMonth = 12,
            ExpYear = 2030,
            Country = "US",
        });
        outcome.ProviderData["fingerprint"].ShouldBe("Xt5EWLLDS7FJjR1c");
    }

    [Fact]
    [Trait("Requirement", "STR-6")]
    public async Task OpenExpiredAndSetupSessions_ArePendingFailedAndSaved()
    {
        var handler = new RecordedExchangeHandler("stripe/session-states.json");
        var context = Context(handler);

        var open = await _stripe.CompleteAsync(context, Complete("cs_test_open"), CancellationToken);
        var expired = await _stripe.CompleteAsync(context, Complete("cs_test_expired"), CancellationToken);
        var setup = await _stripe.CompleteAsync(context, Complete("cs_test_setup"), CancellationToken);

        handler.AssertComplete();
        open.Status.ShouldBe(PaymentStatus.Pending, "the customer came back without finishing");
        (expired.Status, expired.FailureCode).ShouldBe((PaymentStatus.Failed, FailureCode.Abandoned));
        (setup.Status, setup.ProviderPaymentId, setup.SavedCard!.Token, setup.SavedCard.Last4).ShouldBe(
            (PaymentStatus.Succeeded, "seti_1Tahsil", """{"paymentMethodId":"pm_1Master"}""", "4444"));
    }

    [Fact]
    [Trait("Requirement", "STR-8")]
    public async Task ASavedCard_IsChargedOffSession_WithTahsilsIdempotencyKey()
    {
        var handler = new RecordedExchangeHandler("stripe/charge-succeeded.json");

        var outcome = await _stripe.ChargeSavedCardAsync(Context(handler), Charge(), CancellationToken);

        handler.AssertComplete();
        (outcome.Status, outcome.ProviderPaymentId, outcome.SavedCard).ShouldBe((PaymentStatus.Succeeded, "pi_3Renew", null));
    }

    [Fact]
    [Trait("Requirement", "STR-8")]
    public async Task ADecline_KeepsItsPaymentIntent_AndAnAuthenticationRequiredOneIsCanceled()
    {
        var declined = new RecordedExchangeHandler("stripe/charge-insufficient-funds.json");
        var authentication = new RecordedExchangeHandler("stripe/charge-authentication-required.json");

        var insufficient = await _stripe.ChargeSavedCardAsync(Context(declined), Charge(), CancellationToken);
        var required = await _stripe.ChargeSavedCardAsync(Context(authentication), Charge(), CancellationToken);

        declined.AssertComplete();
        authentication.AssertComplete();
        (insufficient.Status, insufficient.FailureCode, insufficient.ProviderFailureCode, insufficient.ProviderPaymentId).ShouldBe(
            (PaymentStatus.Failed, FailureCode.InsufficientFunds, "insufficient_funds", "pi_3Declined"));
        (required.Status, required.FailureCode, required.ProviderPaymentId).ShouldBe((PaymentStatus.Failed, FailureCode.AuthenticationRequired, "pi_3Auth"));
    }

    [Fact]
    [Trait("Requirement", "STR-3")]
    [Trait("Requirement", "ADP-4")]
    public async Task AChargeWithoutAnAnswer_IsRetriedWithItsKey_ThenUnknown()
    {
        var handler = new RecordedExchangeHandler("stripe/charge-unanswered.json");

        var outcome = await _stripe.ChargeSavedCardAsync(Context(handler), Charge(), CancellationToken);

        handler.AssertComplete();
        outcome.Status.ShouldBe(PaymentStatus.Unknown);
    }

    [Fact]
    [Trait("Requirement", "STR-9")]
    public async Task APayment_IsFoundAmongTheCustomersIntents_AndAMissingIntentIsNotFound()
    {
        var handler = new RecordedExchangeHandler("stripe/find-payment.json");
        var context = Context(handler);

        var found = await _stripe.FindPaymentAsync(context, new PaymentQuery(RenewalReference, null, Now) { ProviderCustomer = "cus_TaHs1L" }, CancellationToken);
        var missing = await _stripe.FindPaymentAsync(context, new PaymentQuery(RenewalReference, "pi_3Missing", Now), CancellationToken);
        var nothingToAsk = await _stripe.FindPaymentAsync(context, new PaymentQuery(RenewalReference, null, Now), CancellationToken);

        handler.AssertComplete();
        var outcome = found.ShouldBeOfType<PaymentLookup.Found>().Outcome;
        (outcome.Status, outcome.FailureCode, outcome.ProviderPaymentId).ShouldBe((PaymentStatus.Failed, FailureCode.CardDeclined, "pi_3Renew"));
        missing.ShouldBeOfType<PaymentLookup.NotFound>();
        nothingToAsk.ShouldBeOfType<PaymentLookup.NotFound>();
    }

    [Fact]
    [Trait("Requirement", "STR-10")]
    public async Task ARefund_MayStayPending_AndIsFoundByItsReference()
    {
        var handler = new RecordedExchangeHandler("stripe/refunds.json");
        var context = Context(handler);

        var refunded = await _stripe.RefundAsync(context, new RefundRequest
        {
            RefundId = Guid.Parse("0199c0de-0000-7000-8000-0000000000b1"),
            Reference = RefundReference,
            PaymentReference = Reference,
            ProviderPaymentId = "pi_3Tahsil",
            PaymentAmount = Amount,
            PaymentSucceededAt = Now,
            Amount = Amount with { MinorUnits = 1_000 },
            Reason = "subscription_canceled",
        }, CancellationToken);
        var found = await _stripe.FindRefundAsync(context, new RefundQuery(RefundReference, Reference, null, "pi_3Tahsil", Now), CancellationToken);

        handler.AssertComplete();
        (refunded.Status, refunded.ProviderRefundId, refunded.Kind).ShouldBe((RefundStatus.Pending, "re_1Pending", RefundKind.Refund));
        var outcome = found.ShouldBeOfType<RefundLookup.Found>().Outcome;
        (outcome.Status, outcome.FailureCode).ShouldBe((RefundStatus.Failed, "expired_or_canceled_card"));
    }

    [Fact]
    [Trait("Requirement", "STR-10")]
    public async Task ACardDetached_OrNotAttached_IsRemoved()
    {
        var handler = new RecordedExchangeHandler("stripe/detach.json");
        var context = Context(handler);

        var removed = await _stripe.RemoveSavedCardAsync(context, new SavedCardReference("cus_TaHs1L", Card), CancellationToken);
        var gone = await _stripe.RemoveSavedCardAsync(context, new SavedCardReference("cus_TaHs1L", """{"paymentMethodId":"pm_1Gone"}"""), CancellationToken);

        handler.AssertComplete();
        (removed.Removed, gone.Removed).ShouldBe((true, true));
    }

    [Fact]
    [Trait("Requirement", "STR-12")]
    public async Task ThinEvents_AreReadThroughTheirObjects()
    {
        var handler = new RecordedExchangeHandler("stripe/notifications.json");
        var context = Context(handler);

        var paid = await _stripe.HandleNotificationAsync(context, Event("evt_1", "v1.payment_intent.succeeded", "payment_intent", "pi_3Tahsil"), CancellationToken);
        var expired = await _stripe.HandleNotificationAsync(context, Event("evt_2", "v1.checkout.session.expired", "checkout.session", "cs_test_a1"), CancellationToken);
        var refunded = await _stripe.HandleNotificationAsync(context, Event("evt_3", "v1.refund.updated", "refund", "re_1Pending"), CancellationToken);
        var updated = await _stripe.HandleNotificationAsync(context, Event("evt_4", "v1.payment_method.automatically_updated", "payment_method", "pm_1Visa"), CancellationToken);
        var later = await _stripe.HandleNotificationAsync(context, Event("evt_5", "v1.payment_intent.payment_failed", "payment_intent", "pi_3Later"), CancellationToken);
        var unknown = await _stripe.HandleNotificationAsync(context, Event("evt_6", "v1.customer.updated", "customer", "cus_TaHs1L"), CancellationToken);

        handler.AssertComplete();
        (paid.Verified, paid.ProviderEventId, paid.Response.Status).ShouldBe((true, "evt_1", 200));
        paid.Updates.ShouldBe([new NotificationUpdate.CheckPayment(Reference)]);
        expired.Updates.ShouldBe([new NotificationUpdate.CheckPayment("0199c0de0000700080000000000000a2")]);
        refunded.Updates.ShouldBe([new NotificationUpdate.CheckRefund(RefundReference)]);
        var card = updated.Updates.ShouldHaveSingleItem().ShouldBeOfType<NotificationUpdate.CardUpdated>();
        (card.Token, card.Details.Last4, card.Details.ExpYear).ShouldBe((Card, "1881", 2031));
        (later.Verified, later.Deferred, later.Response.Status, later.Updates.Count).ShouldBe((true, true, 503, 0));
        (unknown.Verified, unknown.Updates.Count, unknown.Response.Status).ShouldBe((true, 0, 200));
    }

    [Fact]
    [Trait("Requirement", "STR-11")]
    public async Task EventsWithoutAValidRecentSignature_AreRejected()
    {
        var context = Context(new RecordedExchangeHandler(new RecordedExchanges("none", [])));

        var forged = await _stripe.HandleNotificationAsync(context, Event("evt_1", "v1.payment_intent.succeeded", "payment_intent", "pi_1", secret: "whsec_other"), CancellationToken);
        var stale = await _stripe.HandleNotificationAsync(context, Event("evt_1", "v1.payment_intent.succeeded", "payment_intent", "pi_1", signedAt: Now.AddMinutes(-6)), CancellationToken);

        (forged.Verified, forged.Response.Status, stale.Verified).ShouldBe((false, 400, false));
    }

    [Fact]
    [Trait("Requirement", "STR-1")]
    public async Task AKeyOfTheOtherMode_FailsTheConnectionTest()
    {
        var context = Context(new RecordedExchangeHandler(new RecordedExchanges("none", [])), environment: ProviderEnvironment.Production);

        (await _stripe.TestConnectionAsync(context, CancellationToken)).FailureCode.ShouldBe("livemode_mismatch");
    }

    private static CompleteRequest Complete(string session) => new()
    {
        PaymentId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a1"),
        Reference = Reference,
        Amount = Amount,
        ProviderData = new Dictionary<string, string> { ["checkoutSessionId"] = session },
        SaveCard = true,
    };

    private static ChargeRequest Charge() => new()
    {
        PaymentId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a3"),
        Reference = RenewalReference,
        Amount = Amount,
        SavedCard = new SavedCardReference("cus_TaHs1L", Card),
        Customer = Customer(),
        Description = "Pro (monthly), renewal",
    };

    /// <summary>A thin event as an event destination sends it, signed at <paramref name="signedAt"/> (now by default).</summary>
    private IncomingNotification Event(string id, string type, string objectType, string objectId, string secret = Secret, DateTimeOffset? signedAt = null)
    {
        var body = Encoding.UTF8.GetBytes(
            $$$"""{"id":"{{{id}}}","object":"v2.core.event","type":"{{{type}}}","livemode":false,"created":"2026-10-01T09:00:00.000Z","related_object":{"id":"{{{objectId}}}","type":"{{{objectType}}}","url":"/v1/{{{objectType}}}s/{{{objectId}}}"}}""");
        var t = (signedAt ?? _time.GetUtcNow()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), (byte[])[.. Encoding.UTF8.GetBytes($"{t}."), .. body]));
        return new IncomingNotification
        {
            Method = "POST",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Stripe-Signature"] = $"t={t},v1={signature}" },
            Body = body,
        };
    }

    private static CustomerInfo Customer() => new()
    {
        Id = Guid.Parse("0199c0de-0000-7000-8000-0000000000c1"),
        Name = "Ada Lovelace",
        Email = "ada@example.com",
        Locale = "en",
    };

    private ProviderContext Context(
        RecordedExchangeHandler handler, IReadOnlyDictionary<string, string>? settings = null, ProviderEnvironment environment = ProviderEnvironment.Sandbox) => new()
        {
            ConnectionId = Guid.Parse("0199c0de-0000-7000-8000-00000000c001"),
            Environment = environment,
            Credentials = new Dictionary<string, string> { ["secretKey"] = "sk_test_tahsil", ["webhookSigningSecret"] = Secret },
            Settings = settings ?? new Dictionary<string, string>(),
            Http = handler.Client(),
            Time = _time,
            Logger = NullLogger.Instance,
            NotificationUrl = new Uri("https://billing.example.com/provider-notifications/0199c0de-0000-7000-8000-00000000c001"),
        };
}
