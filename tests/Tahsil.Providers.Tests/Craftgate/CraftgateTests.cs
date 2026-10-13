using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Craftgate;
using Tahsil.Providers.Tests.Recorded;

namespace Tahsil.Providers.Tests.Craftgate;

/// <summary>The Craftgate adapter against its signature vectors and exchanges from Craftgate's documentation and SDKs (docs/specs/providers/craftgate.md).</summary>
public sealed class CraftgateTests
{
    private const string Reference = "0199c0de0000700080000000000000a1";
    private const string RenewalReference = "0199c0de0000700080000000000000a3";
    private const string WebhookKey = "1Q2w3E4r5T6y7U8i9Op";
    private const string Card = """{"cardUserKey":"cuk-1","cardToken":"ct-1"}""";

    private static readonly Amount Amount = new(29_900, "TRY", 2);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly CraftgateProvider _craftgate = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "CRG-4")]
    [Trait("Requirement", "CRG-12")]
    public void Signatures_FollowTheVectors()
    {
        CraftgateSigning.Request(
            "https://api.craftgate.io/onboarding/v1/members", "api-key", "secret-key", "rand-2010",
            """{"memberExternalId":"ext-1511","name":"Haluk Demir","email":"haluk.demir@example.com","phoneNumber":"905551111111"}""")
            .ShouldBe("9guTlRNDTRRyUsfSyJ8N/gAdNgQgUsL1guzqgG+FzN4=");
        CraftgateSigning.Request("https://api.craftgate.io/onboarding/v1/members", "api-key", "secret-key", "rand-2010", null).ShouldBe("/IoWMs1tQ329eEC4XoDwQAhYc4eHZKV8bdFYNhrL/BQ=");
        CraftgateSigning.Request("https://api.craftgate.io/onboarding/v1/members/1", "key-1", "FooBar123!", "Xa15Fp11T", null).ShouldBe("y1TtnjNCJEvlkP5ufCkK3H0i2guMB/bKL4Ayw3VlKWA=");
        CraftgateSigning.Webhook("1Q2w3E4r5T6y7U8i9Op", "API_AUTH", "1641018632", "SUCCESS", "2150001").ShouldBe("eNXKxfxUpVmp/wBrNUmOLjNXL0sYl0mh1s/rEB8K8NU=");
        CraftgateSigning.Webhook("Aoh7tReTybO6wOjBmOJFFsOR53SBojEp", "API_VERIFY_AND_AUTH", "1661521221", "SUCCESS", "584").ShouldBe("0wRB5XqWJxwwPbn5Z9TcbHh8EGYFufSYTsRMB74N094=");
    }

    [Theory]
    [InlineData("NOT_SUFFICIENT_FUNDS", FailureCode.InsufficientFunds)]
    [InlineData("DO_NOT_HONOUR", FailureCode.CardDeclined)]
    [InlineData("NOT_PERMITTED_TO_FOREIGN_CARD", FailureCode.CardNotSupported)]
    [InlineData("EXPIRED_CARD", FailureCode.ExpiredCard)]
    [InlineData("INVALID_CVC2", FailureCode.InvalidCard)]
    [InlineData("STOLEN_CARD", FailureCode.LostOrStolen)]
    [InlineData("FRAUD_CHECK_BLOCK", FailureCode.FraudSuspected)]
    [InlineData("CVC2_MAX_ATTEMPT", FailureCode.LimitExceeded)]
    [InlineData("DEBIT_CARDS_REQUIRES_3DS", FailureCode.AuthenticationRequired)]
    [InlineData("INVALID_CAVV", FailureCode.AuthenticationFailed)]
    [InlineData("CARDHOLDER_GAVE_UP", FailureCode.Abandoned)]
    [InlineData("CVC_REQUIRED", FailureCode.MerchantNotPermitted)]
    [InlineData("REQUEST_BLOCKED_BY_BANK", FailureCode.ProviderError)]
    [InlineData("ISSUER_OR_SWITCH_INOPERATIVE", FailureCode.ProviderUnavailable)]
    [InlineData(null, FailureCode.InvalidRequest)]
    public void Failures_MapByErrorGroup(string? group, FailureCode failure) => CraftgateFailures.Map(group, "1").ShouldBe(failure);

    [Fact]
    [Trait("Requirement", "CRG-3")]
    [Trait("Requirement", "CRG-6")]
    [Trait("Requirement", "CRG-7")]
    [Trait("Requirement", "CRG-8")]
    [Trait("Requirement", "CRG-13")]
    public async Task ACheckout_IsPaidOnTheCommonPaymentPage_AndStoresTheCardWithItsExpiry()
    {
        var handler = new RecordedExchangeHandler("craftgate/checkout.json");
        var context = Context(handler);

        var started = await _craftgate.StartAsync(context, new StartRequest
        {
            PaymentId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a1"),
            Reference = Reference,
            Amount = Amount,
            Purpose = PaymentPurpose.Charge,
            SaveCard = true,
            Customer = new CustomerInfo { Id = Guid.Parse("0199c0de-0000-7000-8000-0000000000c1"), Email = "ayse@example.com" },
            Description = "Pro (Aylık)",
            ReturnUrl = new Uri("https://billing.example.com/checkout/cs_x/return/0199c0de-0000-7000-8000-0000000000a1"),
            CancelUrl = new Uri("https://billing.example.com/checkout/cs_x"),
            Locale = "tr",
        }, CancellationToken);
        var paid = await _craftgate.CompleteAsync(context, Complete("a1b2c3d4-e5f6", posted: "a1b2c3d4-e5f6"), CancellationToken);
        var failed = await _craftgate.CompleteAsync(context, Complete("f0-failed"), CancellationToken);
        var expired = await _craftgate.CompleteAsync(context, Complete("f0-expired"), CancellationToken);
        var replayed = await _craftgate.CompleteAsync(context, Complete("a1b2c3d4-e5f6", posted: "another-payments-token"), CancellationToken);

        handler.AssertComplete();
        handler.Requests[0].Body!.ShouldContain("\"price\":299.00,", Case.Sensitive, "amounts are major units with two fraction digits");
        started.Presentation.ShouldBe(new Presentation.Redirect(new Uri("https://sandbox-ode.craftgate.io/payment?token=a1b2c3d4-e5f6&lang=tr")));
        started.ProviderData["token"].ShouldBe("a1b2c3d4-e5f6");
        (paid.Status, paid.ProviderPaymentId, paid.ChargedAmount, paid.ProviderData["paymentTransactionId"]).ShouldBe((PaymentStatus.Succeeded, "1500", Amount, "2001"));
        paid.SavedCard.ShouldBe(new SavedCard
        {
            ProviderCustomer = "cuk-1",
            Token = Card,
            Bin = "52092200",
            Last4 = "0002",
            Brand = "mastercard",
            Funding = "credit",
            Issuer = "Kuveyt Türk",
            CardFamily = "Saglam Kart",
            ExpMonth = 12,
            ExpYear = 2030,
        });
        (failed.Status, failed.FailureCode, failed.ProviderFailureMessage).ShouldBe((PaymentStatus.Failed, FailureCode.InsufficientFunds, "Kart limiti yetersiz"));
        (expired.Status, expired.FailureCode).ShouldBe((PaymentStatus.Failed, FailureCode.Abandoned));
        (replayed.Status, replayed.FailureCode).ShouldBe((PaymentStatus.Failed, FailureCode.InvalidRequest), "a token posted back must be this payment's");
    }

    [Fact]
    [Trait("Requirement", "CRG-9")]
    public async Task Renewals_AreChargedOnTheChosenPos_AndTheirFailuresMapped()
    {
        var handler = new RecordedExchangeHandler("craftgate/card-payments.json");
        var context = Context(handler, new Dictionary<string, string> { ["posAlias"] = "62-kuveytturk-12" });

        var outcomes = new List<PaymentOutcome>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            outcomes.Add(await _craftgate.ChargeSavedCardAsync(context, Charge(), CancellationToken));
        }

        handler.AssertComplete();
        (outcomes[0].Status, outcomes[0].ProviderPaymentId, outcomes[0].ProviderData["paymentTransactionId"]).ShouldBe((PaymentStatus.Succeeded, "1600", "2101"));
        (outcomes[1].Status, outcomes[1].FailureCode).ShouldBe((PaymentStatus.Failed, FailureCode.InsufficientFunds));
        (outcomes[2].Status, outcomes[2].FailureCode).ShouldBe((PaymentStatus.Failed, FailureCode.MerchantNotPermitted), "the POS does not allow payments without CVC");
        outcomes[3].Status.ShouldBe(PaymentStatus.Unknown, "an earlier sale with this reference succeeded");
    }

    [Fact]
    [Trait("Requirement", "CRG-10")]
    public async Task AnUnansweredRenewal_IsSentOnceMoreWithItsKey()
    {
        var handler = new RecordedExchangeHandler("craftgate/card-payment-replay.json");
        var context = Context(handler, new Dictionary<string, string> { ["posAlias"] = "62-kuveytturk-12" }, TimeProvider.System);

        var outcome = await _craftgate.ChargeSavedCardAsync(context, Charge(), CancellationToken);

        handler.AssertComplete();
        (outcome.Status, outcome.ProviderPaymentId).ShouldBe((PaymentStatus.Succeeded, "1600"));
    }

    [Fact]
    [Trait("Requirement", "CRG-11")]
    public async Task Payments_AreFoundByReference_SuccessesFirst_AndCardsAreDeleted()
    {
        var handler = new RecordedExchangeHandler("craftgate/lookups.json");
        var context = Context(handler);

        var found = await _craftgate.FindPaymentAsync(context, new PaymentQuery(Reference, null, _time.GetUtcNow()), CancellationToken);
        var none = await _craftgate.FindPaymentAsync(context, new PaymentQuery(Reference, null, _time.GetUtcNow()), CancellationToken);
        var removed = await _craftgate.RemoveSavedCardAsync(context, new SavedCardReference("cuk-1", Card), CancellationToken);

        handler.AssertComplete();
        var outcome = found.ShouldBeOfType<PaymentLookup.Found>().Outcome;
        (outcome.Status, outcome.ProviderPaymentId).ShouldBe((PaymentStatus.Succeeded, "1500"));
        none.ShouldBeOfType<PaymentLookup.NotFound>();
        removed.Removed.ShouldBeTrue();
    }

    [Fact]
    public async Task AFullRefund_IsACancelOnItsDay_AndAPartialOneWaitsForTheDaysEnd()
    {
        var handler = new RecordedExchangeHandler("craftgate/refunds.json");
        var context = Context(handler);

        var full = await _craftgate.RefundAsync(context, Refund("0199c0de0000700080000000000000b1", Amount), CancellationToken);
        var partial = await _craftgate.RefundAsync(context, Refund("0199c0de0000700080000000000000b2", Amount with { MinorUnits = 5_000 }), CancellationToken);
        var waiting = await _craftgate.FindRefundAsync(context, new RefundQuery("0199c0de0000700080000000000000b2", Reference, null, "1500", _time.GetUtcNow()), CancellationToken);

        handler.AssertComplete();
        (full.Status, full.Kind, full.ProviderRefundId).ShouldBe((RefundStatus.Succeeded, RefundKind.Void, "31"));
        (partial.Status, partial.FailureCode).ShouldBe((RefundStatus.Failed, "refund.not_allowed_yet"));
        waiting.ShouldBeOfType<RefundLookup.Found>().Outcome.Status.ShouldBe(RefundStatus.Pending);
    }

    [Fact]
    [Trait("Requirement", "CRG-1")]
    public async Task TheConnectionTest_ChecksTheKeys_AndThePosRenewalsUse()
    {
        var handler = new RecordedExchangeHandler("craftgate/connection.json");

        var pos = await _craftgate.TestConnectionAsync(Context(handler, new Dictionary<string, string> { ["posAlias"] = "62-kuveytturk-12" }), CancellationToken);
        var keys = await _craftgate.TestConnectionAsync(Context(handler), CancellationToken);

        handler.AssertComplete();
        (pos.Succeeded, pos.FailureCode, pos.Message).ShouldBe((false, "pos_not_ready", "The POS 62-kuveytturk-12 cannot charge renewals: payments without CVC are not enabled."));
        (keys.Succeeded, keys.FailureCode).ShouldBe((false, "invalid_credentials"));
    }

    [Fact]
    [Trait("Requirement", "CRG-12")]
    public async Task Webhooks_AreVerified_AndReadAsHints()
    {
        var handler = new RecordedExchangeHandler("craftgate/webhooks.json");
        var context = Context(handler);

        var checkout = await _craftgate.HandleNotificationAsync(context, Webhook("CHECKOUTFORM_AUTH", "a1b2c3d4-e5f6"), CancellationToken);
        var refund = await _craftgate.HandleNotificationAsync(context, Webhook("REFUND", "31"), CancellationToken);
        var later = await _craftgate.HandleNotificationAsync(context, Webhook("API_AUTH", "1600"), CancellationToken);
        var other = await _craftgate.HandleNotificationAsync(context, Webhook("PAYOUT_COMPLETED", "9"), CancellationToken);
        var forged = await _craftgate.HandleNotificationAsync(context, Webhook("API_AUTH", "1600", signature: "bm9wZQ=="), CancellationToken);

        handler.AssertComplete();
        (checkout.Verified, checkout.ProviderEventId).ShouldBe((true, "CHECKOUTFORM_AUTH:a1b2c3d4-e5f6:1791000000"));
        checkout.Updates.ShouldBe([new NotificationUpdate.CheckPayment(Reference)]);
        refund.Updates.ShouldBe([new NotificationUpdate.CheckRefund("0199c0de0000700080000000000000b1")]);
        (later.Deferred, later.Response.Status).ShouldBe((true, 503));
        (other.Verified, other.Updates.Count, other.Response.Status).ShouldBe((true, 0, 200));
        (forged.Verified, forged.Response.Status).ShouldBe((false, 400));
    }

    private static CompleteRequest Complete(string token, string? posted = null) => new()
    {
        PaymentId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a1"),
        Reference = Reference,
        Amount = Amount,
        SaveCard = true,
        ProviderData = new Dictionary<string, string> { ["token"] = token },
        ReturnValues = posted is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["token"] = posted },
    };

    private static ChargeRequest Charge() => new()
    {
        PaymentId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a3"),
        Reference = RenewalReference,
        Amount = Amount,
        SavedCard = new SavedCardReference("cuk-1", Card),
        Customer = new CustomerInfo { Id = Guid.Parse("0199c0de-0000-7000-8000-0000000000c1"), IpAddress = "85.34.78.112" },
        Description = "Pro (Aylık), yenileme",
    };

    private RefundRequest Refund(string reference, Amount amount) => new()
    {
        RefundId = Guid.CreateVersion7(),
        Reference = reference,
        PaymentReference = Reference,
        ProviderPaymentId = "1500",
        PaymentAmount = Amount,
        PaymentSucceededAt = _time.GetUtcNow(),
        Amount = amount,
        PaymentProviderData = new Dictionary<string, string> { ["paymentTransactionId"] = "2001" },
    };

    /// <summary>A webhook as Craftgate posts it, signed with the merchant webhook key unless another signature is given.</summary>
    private static IncomingNotification Webhook(string eventType, string payloadId, string? signature = null)
    {
        var body = new JsonObject { ["eventType"] = eventType, ["eventTimestamp"] = 1791000000, ["status"] = "SUCCESS", ["payloadId"] = payloadId }.ToJsonString();
        return new IncomingNotification
        {
            Method = "POST",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-cg-signature-v1"] = signature ?? CraftgateSigning.Webhook(WebhookKey, eventType, "1791000000", "SUCCESS", payloadId),
            },
            Body = Encoding.UTF8.GetBytes(body),
        };
    }

    private ProviderContext Context(RecordedExchangeHandler handler, IReadOnlyDictionary<string, string>? settings = null, TimeProvider? time = null) => new()
    {
        ConnectionId = Guid.Parse("0199c0de-0000-7000-8000-00000000c003"),
        Environment = ProviderEnvironment.Sandbox,
        Credentials = new Dictionary<string, string> { ["apiKey"] = "sandbox-api-key", ["secretKey"] = "sandbox-secret-key", ["webhookKey"] = WebhookKey },
        Settings = settings ?? new Dictionary<string, string>(),
        Http = handler.Client(),
        Time = time ?? _time,
        Logger = NullLogger.Instance,
        NotificationUrl = new Uri("https://billing.example.com/provider-notifications/0199c0de-0000-7000-8000-00000000c003"),
    };
}
