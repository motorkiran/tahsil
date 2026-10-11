using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Iyzico;
using Tahsil.Providers.Tests.Recorded;

namespace Tahsil.Providers.Tests.Iyzico;

/// <summary>The iyzico adapter against its signature vectors and recorded exchanges (docs/specs/providers/iyzico.md).</summary>
public sealed class IyzicoTests
{
    private const string Reference = "0199c0de0000700080000000000000a1";
    private const string RefundReference = "0199c0de0000700080000000000000b2";
    private const string Token = "f7a5e8a2-2f3c-4b1e-9c11-0d2b8e2c6a10";

    private static readonly Guid PaymentId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a1");
    private static readonly Amount Amount = new(29_900, "TRY", 2);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly IyzicoProvider _iyzico = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "IYZ-2")]
    public void Requests_AreSignedAsTheVectorSays() =>
        IyzicoSigning.Authorization("sandbox-apiKey", "sandbox-secretKey", "123456789", "/payment/3dsecure/auth", """{"locale":"tr","paymentId":"1"}""")
            .ShouldBe("IYZWSv2 YXBpS2V5OnNhbmRib3gtYXBpS2V5JnJhbmRvbUtleToxMjM0NTY3ODkmc2lnbmF0dXJlOjMwOTlhMjUwY2VkOTRjZjFkMGM2YWYxOWUzNDlhMWViZTMzMDk1NTM2NmUxOGMxM2U3NDg5MjEyYjI3NDU5ZGY=");

    [Fact]
    [Trait("Requirement", "IYZ-4")]
    public void ResponseSignatures_AreCheckedAsTheVectorSays()
    {
        IyzicoSigning.ResponseMatches(
            "sandbox-qaIiLIxhjMgx3LSKIVvp6j17NunHOFtD", "836c3a6c8db86c81043f2ca74edb13518b54a813f454f8dd762f0dd658610173", "22416032", "TRY", "basketId", "conversationId", "10.5", "10.5")
            .ShouldBeTrue();
        IyzicoSigning.ResponseMatches("sandbox-qaIiLIxhjMgx3LSKIVvp6j17NunHOFtD", "836c3a6c8db86c81043f2ca74edb13518b54a813f454f8dd762f0dd658610174", "22416032", "TRY", "basketId", "conversationId", "10.5", "10.5")
            .ShouldBeFalse();
    }

    [Theory]
    [Trait("Requirement", "IYZ-4")]
    [Trait("Requirement", "IYZ-14")]
    [InlineData("10.50", "10.5")]
    [InlineData("10.0", "10")]
    [InlineData("0.20800000", "0.208")]
    [InlineData("0E-8", "0")]
    public void Prices_LoseTrailingZerosInSignatures(string price, string normalized) =>
        IyzicoSigning.Price(decimal.Parse(price, NumberStyles.Float, CultureInfo.InvariantCulture)).ShouldBe(normalized);

    [Theory]
    [InlineData("NOT_SUFFICIENT_FUNDS", "10051", false, FailureCode.InsufficientFunds)]
    [InlineData("DO_NOT_HONOUR", "10005", false, FailureCode.CardDeclined)]
    [InlineData(null, "5184", false, FailureCode.CardNotSupported)]
    [InlineData("STOLEN_CARD", "10043", false, FailureCode.LostOrStolen)]
    [InlineData(null, "5072", false, FailureCode.AuthenticationRequired)]
    [InlineData(null, "5072", true, FailureCode.AuthenticationFailed)]
    [InlineData(null, "3007", false, FailureCode.MerchantNotPermitted)]
    [InlineData(null, "1001", false, FailureCode.InvalidRequest)]
    [InlineData(null, "5062", false, FailureCode.InvalidRequest)]
    [InlineData(null, "10219", false, FailureCode.ProviderUnavailable)]
    [InlineData(null, "9000", false, FailureCode.ProviderError)]
    [InlineData(null, "77777", false, FailureCode.ProviderError)]
    public void Failures_MapByGroupThenCode(string? group, string code, bool customerPresent, FailureCode failure) =>
        IyzicoFailures.Map(group, code, customerPresent).ShouldBe(failure);

    [Fact]
    [Trait("Requirement", "IYZ-6")]
    public void TheBuyer_IsTheCustomer_WithTheConnectionsFallbacks()
    {
        var context = Context(new RecordedExchangeHandler(new RecordedExchanges("none", [])), new Dictionary<string, string> { ["identityNumberFallback"] = "22222222222" });

        var person = IyzicoBuyer.Buyer(context, Customer());
        var company = IyzicoBuyer.Buyer(context, new CustomerInfo { Id = Guid.Empty, Name = "Acme Yazılım A.Ş.", IsCompany = true, TaxId = "1234567890", Country = "TR", City = "İzmir", AddressLine = "Alsancak" });

        (person["name"]!.GetValue<string>(), person["surname"]!.GetValue<string>(), person["identityNumber"]!.GetValue<string>()).ShouldBe(("Ayşe Nur", "Yılmaz", "22222222222"));
        (person["registrationAddress"]!.GetValue<string>(), person["country"]!.GetValue<string>(), person["gsmNumber"]!.GetValue<string>()).ShouldBe(("Istanbul, Turkey", "Turkey", "+905321234567"));
        (company["name"]!.GetValue<string>(), company["surname"]!.GetValue<string>(), company["identityNumber"]!.GetValue<string>(), company["country"]!.GetValue<string>())
            .ShouldBe(("Acme Yazılım A.Ş.", "-", "1234567890", "Turkey"));
    }

    [Theory]
    [Trait("Requirement", "IYZ-6")]
    [InlineData("test-connection", true)]
    [InlineData("test-connection-wrong-keys", false)]
    public async Task TheConnectionTest_ProvesTheKeys(string recording, bool succeeds)
    {
        var handler = new RecordedExchangeHandler($"iyzico/{recording}.json");

        var result = await _iyzico.TestConnectionAsync(Context(handler), CancellationToken);

        result.Succeeded.ShouldBe(succeeds);
        if (!succeeds)
        {
            result.FailureCode.ShouldBe("invalid_credentials");
        }

        handler.AssertComplete();
    }

    [Fact]
    [Trait("Requirement", "IYZ-6")]
    public async Task Start_SendsTheCustomerToTheCheckoutForm()
    {
        var handler = new RecordedExchangeHandler("iyzico/cf-initialize-success.json");

        var started = await _iyzico.StartAsync(Context(handler), StartRequest(), CancellationToken);

        started.Presentation.ShouldBeOfType<Presentation.Redirect>().Url.ShouldBe(new Uri($"https://sandbox-cpp.iyzipay.com?token={Token}&lang=tr"));
        started.ProviderData["token"].ShouldBe(Token);
        started.ExpiresAt.ShouldBe(_time.GetUtcNow().AddMinutes(30));
        var sent = JsonNode.Parse(handler.Requests.Single().Body!)!;
        (sent["basketItems"]![0]!["itemType"]!.GetValue<string>(), sent["basketItems"]![0]!["price"]!.GetValue<string>()).ShouldBe(("VIRTUAL", "299.00"));
        sent["buyer"]!["email"]!.GetValue<string>().ShouldBe("ayse@example.com");
        sent["buyer"]!.AsObject().ContainsKey("zipCode").ShouldBeFalse("null members are left out");
        handler.Requests.Single().Headers["Authorization"].ShouldStartWith("IYZWSv2 ");
        handler.AssertComplete();
    }

    [Fact]
    [Trait("Requirement", "IYZ-6")]
    public async Task AStartIyzicoRefuses_FailsWithoutCharging()
    {
        var handler = new RecordedExchangeHandler("iyzico/cf-initialize-auth-error.json");

        var started = await _iyzico.StartAsync(Context(handler), StartRequest(), CancellationToken);

        started.Presentation.ShouldBeNull();
        (started.Failure!.Status, started.Failure.FailureCode, started.Failure.ProviderFailureCode).ShouldBe((PaymentStatus.Failed, FailureCode.InvalidRequest, "1001"));
    }

    [Fact]
    [Trait("Requirement", "IYZ-7")]
    [Trait("Requirement", "IYZ-8")]
    public async Task Complete_ReadsTheResult_AndTheSavedCardWithItsBankAndExpiry()
    {
        var handler = new RecordedExchangeHandler("iyzico/cf-retrieve-saved-card.json");

        var outcome = await _iyzico.CompleteAsync(Context(handler), CompleteRequest(), CancellationToken);

        (outcome.Status, outcome.ProviderPaymentId, outcome.ThreeDSecure).ShouldBe((PaymentStatus.Succeeded, "22416032", ThreeDSecureResult.Authenticated));
        outcome.ChargedAmount.ShouldBe(Amount);
        outcome.ProviderData["paymentTransactionId"].ShouldBe("23938523");
        var card = outcome.SavedCard.ShouldNotBeNull();
        (card.ProviderCustomer, card.Token, card.Brand, card.Funding, card.Bin, card.Last4).ShouldBe(("cuk-1", "ct-1", "mastercard", "credit", "552879", "0008"));
        (card.Issuer, card.ExpMonth, card.ExpYear, card.CardFamily).ShouldBe(("Garanti Bankası", (int?)12, (int?)2030, "Bonus"));
        handler.AssertComplete();
    }

    [Theory]
    [Trait("Requirement", "IYZ-7")]
    [InlineData("cf-retrieve-no-card", PaymentStatus.Succeeded, null)]
    [InlineData("cf-retrieve-fraud-review", PaymentStatus.Pending, null)]
    [InlineData("cf-retrieve-failure", PaymentStatus.Failed, FailureCode.InsufficientFunds)]
    [InlineData("cf-retrieve-token-expired", PaymentStatus.Failed, FailureCode.Abandoned)]
    [InlineData("cf-retrieve-bad-signature", PaymentStatus.Unknown, null)]
    public async Task Complete_MapsEachResult(string recording, PaymentStatus status, FailureCode? failure)
    {
        var handler = new RecordedExchangeHandler($"iyzico/{recording}.json");

        var outcome = await _iyzico.CompleteAsync(Context(handler), CompleteRequest(), CancellationToken);

        (outcome.Status, outcome.FailureCode, outcome.SavedCard).ShouldBe((status, failure, null));
        handler.AssertComplete();
    }

    [Fact]
    [Trait("Requirement", "IYZ-7")]
    public async Task Complete_RefusesAReturnForAnotherForm()
    {
        var handler = new RecordedExchangeHandler(new RecordedExchanges("none", []));

        var outcome = await _iyzico.CompleteAsync(Context(handler), CompleteRequest() with { ReturnValues = new Dictionary<string, string> { ["token"] = "someone-elses" } }, CancellationToken);

        outcome.Status.ShouldBe(PaymentStatus.Unknown);
        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Requirement", "IYZ-9")]
    [InlineData("charge-success", PaymentStatus.Succeeded, null)]
    [InlineData("charge-insufficient-funds", PaymentStatus.Failed, FailureCode.InsufficientFunds)]
    [InlineData("charge-requires-3ds", PaymentStatus.Failed, FailureCode.AuthenticationRequired)]
    [InlineData("charge-storage-disabled", PaymentStatus.Failed, FailureCode.MerchantNotPermitted)]
    [InlineData("charge-timeout", PaymentStatus.Unknown, null)]
    public async Task SavedCardCharges_MapEachAnswer(string recording, PaymentStatus status, FailureCode? failure)
    {
        var handler = new RecordedExchangeHandler($"iyzico/{recording}.json");

        var outcome = await _iyzico.ChargeSavedCardAsync(Context(handler), new ChargeRequest
        {
            PaymentId = PaymentId,
            Reference = Reference,
            Amount = Amount,
            SavedCard = new SavedCardReference("cuk-1", "ct-1"),
            Customer = Customer(),
            Description = "Pro, monthly",
        }, CancellationToken);

        (outcome.Status, outcome.FailureCode).ShouldBe((status, failure));
        if (status == PaymentStatus.Succeeded)
        {
            (outcome.ProviderPaymentId, outcome.ProviderData["paymentTransactionId"], outcome.ThreeDSecure).ShouldBe(("22416099", "23938599", ThreeDSecureResult.NotAttempted));
        }

        handler.AssertComplete();
    }

    [Theory]
    [Trait("Requirement", "IYZ-10")]
    [InlineData("report-success", "found", PaymentStatus.Succeeded)]
    [InlineData("report-failed", "found", PaymentStatus.Failed)]
    [InlineData("report-pending", "found", PaymentStatus.Pending)]
    [InlineData("report-empty", "not_found", null)]
    public async Task Payments_AreFoundByReference(string recording, string result, PaymentStatus? status)
    {
        var handler = new RecordedExchangeHandler($"iyzico/{recording}.json");

        var lookup = await _iyzico.FindPaymentAsync(Context(handler), new PaymentQuery(Reference, null, _time.GetUtcNow()), CancellationToken);

        if (result == "not_found")
        {
            lookup.ShouldBeOfType<PaymentLookup.NotFound>();
        }
        else
        {
            lookup.ShouldBeOfType<PaymentLookup.Found>().Outcome.Status.ShouldBe(status!.Value);
        }

        handler.AssertComplete();
    }

    [Fact]
    [Trait("Requirement", "IYZ-11")]
    public async Task ASameDayFullRefund_CancelsThePayment()
    {
        var handler = new RecordedExchangeHandler("iyzico/cancel.json");

        var refund = await _iyzico.RefundAsync(Context(handler), RefundRequest(Amount, paidAt: _time.GetUtcNow().AddHours(-2)), CancellationToken);

        (refund.Status, refund.Kind, refund.ProviderRefundId).ShouldBe((RefundStatus.Succeeded, RefundKind.Void, "cancel-host-1"));
        handler.AssertComplete();
    }

    [Theory]
    [Trait("Requirement", "IYZ-11")]
    [InlineData("refund", RefundStatus.Succeeded, null)]
    [InlineData("refund-day-end", RefundStatus.Failed, "refund.not_allowed_yet")]
    public async Task OtherRefunds_RefundTheTransaction(string recording, RefundStatus status, string? failure)
    {
        var handler = new RecordedExchangeHandler($"iyzico/{recording}.json");

        var refund = await _iyzico.RefundAsync(Context(handler), RefundRequest(new Amount(10_000, "TRY", 2), paidAt: _time.GetUtcNow().AddHours(-1)), CancellationToken);

        (refund.Status, refund.Kind, refund.FailureCode).ShouldBe((status, RefundKind.Refund, failure));
        handler.AssertComplete();
    }

    [Fact]
    public async Task SavedCards_AreDeleted()
    {
        var handler = new RecordedExchangeHandler("iyzico/card-delete.json");

        var removed = await _iyzico.RemoveSavedCardAsync(Context(handler), new SavedCardReference("cuk-1", "ct-1"), CancellationToken);

        removed.Removed.ShouldBeTrue();
        handler.AssertComplete();
    }

    [Theory]
    [Trait("Requirement", "IYZ-12")]
    [Trait("Requirement", "IYZ-13")]
    [InlineData("CHECKOUT_FORM_AUTH", true)]
    [InlineData("API_AUTH", true)]
    [InlineData("API_AUTH", false)]
    public async Task Notifications_AreVerified_AndOnlyTriggerAPaymentCheck(string eventType, bool validSignature)
    {
        var body = new JsonObject
        {
            ["paymentConversationId"] = Reference,
            ["merchantId"] = 3388,
            ["paymentId"] = 22416032,
            ["iyziPaymentId"] = 22416032,
            ["token"] = Token,
            ["status"] = "SUCCESS",
            ["iyziReferenceCode"] = "ref-code-1",
            ["iyziEventType"] = eventType,
            ["iyziEventTime"] = 1791000000000,
        };
        var signed = eventType == "CHECKOUT_FORM_AUTH"
            ? $"sandbox-secret-key{eventType}22416032{Token}{Reference}SUCCESS"
            : $"sandbox-secret-key{eventType}22416032{Reference}SUCCESS";
        var signature = validSignature ? Hmac.Hex(HmacAlgorithm.Sha256, "sandbox-secret-key", signed) : new string('0', 64);

        var result = await _iyzico.HandleNotificationAsync(Context(new RecordedExchangeHandler(new RecordedExchanges("none", []))), new IncomingNotification
        {
            Method = "POST",
            Headers = new Dictionary<string, string> { ["x-iyz-signature-v3"] = signature },
            Body = Encoding.UTF8.GetBytes(body.ToJsonString()),
        }, CancellationToken);

        result.Verified.ShouldBe(validSignature);
        if (validSignature)
        {
            (result.ProviderEventId, result.Response.Status).ShouldBe(("ref-code-1", 200));
            result.Updates.ShouldHaveSingleItem().ShouldBe(new NotificationUpdate.CheckPayment(Reference));
        }
        else
        {
            result.Response.Status.ShouldBe(400);
        }
    }

    private ProviderContext Context(RecordedExchangeHandler handler, IReadOnlyDictionary<string, string>? settings = null) => new()
    {
        ConnectionId = Guid.Parse("0199c0de-0000-7000-8000-00000000c001"),
        Environment = ProviderEnvironment.Sandbox,
        Credentials = new Dictionary<string, string> { ["apiKey"] = "sandbox-api-key", ["secretKey"] = "sandbox-secret-key" },
        Settings = settings ?? new Dictionary<string, string>(),
        Http = handler.Client(),
        Time = _time,
        Logger = NullLogger.Instance,
        NotificationUrl = new Uri("https://billing.example.com/provider-notifications/0199c0de-0000-7000-8000-00000000c001"),
    };

    private static CustomerInfo Customer() => new()
    {
        Id = Guid.Parse("0199c0de-0000-7000-8000-0000000000c1"),
        Name = "Ayşe Nur Yılmaz",
        Email = "ayse@example.com",
        Phone = "0532 123 45 67",
        IpAddress = "203.0.113.7",
    };

    private static StartRequest StartRequest() => new()
    {
        PaymentId = PaymentId,
        Reference = Reference,
        Amount = Amount,
        Purpose = PaymentPurpose.Charge,
        Customer = Customer(),
        Description = "1,000 credits × 1",
        ReturnUrl = new Uri($"https://billing.example.com/checkout/cs_1/return/{PaymentId}"),
        CancelUrl = new Uri("https://billing.example.com/checkout/cs_1"),
    };

    private CompleteRequest CompleteRequest() => new()
    {
        PaymentId = PaymentId,
        Reference = Reference,
        Amount = Amount,
        ProviderData = new Dictionary<string, string> { ["token"] = Token, ["tokenExpiresAt"] = _time.GetUtcNow().AddMinutes(30).ToString("O", CultureInfo.InvariantCulture) },
        ReturnValues = new Dictionary<string, string> { ["token"] = Token },
    };

    private static RefundRequest RefundRequest(Amount amount, DateTimeOffset paidAt) => new()
    {
        RefundId = Guid.CreateVersion7(),
        Reference = RefundReference,
        PaymentReference = Reference,
        ProviderPaymentId = "22416099",
        PaymentAmount = Amount,
        PaymentSucceededAt = paidAt,
        Amount = amount,
        PaymentProviderData = new Dictionary<string, string> { ["paymentTransactionId"] = "23938599" },
    };
}
