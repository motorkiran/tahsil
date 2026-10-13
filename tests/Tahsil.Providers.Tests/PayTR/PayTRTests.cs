using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.PayTR;
using Tahsil.Providers.Tests.Recorded;

namespace Tahsil.Providers.Tests.PayTR;

/// <summary>The PayTR adapter, in iFrame and direct form mode, against its hash vectors and exchanges from PayTR's documentation (docs/specs/providers/paytr.md).</summary>
public sealed class PayTRTests
{
    private const string Reference = "0199c0de0000700080000000000000a1";
    private const string RefundReference = "0199c0de0000700080000000000000b1";
    private const string Key = "KKKEEEYYY";
    private const string Salt = "XXXYYYZZZ";

    private static readonly Amount Amount = new(29_900, "TRY", 2);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly PayTRProvider _paytr = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "PTR-4")]
    public void Tokens_MatchPayTRsHashCalculator()
    {
        PayTRTokens.IFrame(Key, Salt, "123456", "1.1.1.1", "ABC123", "user@domainname.com", "100", "W1siXHUwMGQ2cm5layBcdTAwZmNyXHUwMGZjbiAxIiwiMTguMDAiLDFdXQ==", "0", "12", "TL", "0")
            .ShouldBe("0+KX6R/dqqZmebf2EqQqJ1LfoT5kzyuE+6GswB3znyY=");
        PayTRTokens.Direct(Key, Salt, "123456", "1.1.1.1", "ABC123", "user@domainname.com", "100.99", "card", "0", "TL", "1", "1")
            .ShouldBe("3Hs63FHJBHH8Zh6opMVekDcacPPXqDehiFyEob/jyWc=");
        PayTRTokens.Refund(Key, Salt, "123456", "ABC123", "10.25").ShouldBe("WfEpWCGkOI/J9Vl3LZ5yC+KXpq2xD4Cl+dN/wBuw5AA=");
        PayTRTokens.Status(Key, Salt, "123456", "ABC123").ShouldBe("590yqOee9lWNUk4UpzlfFGNG55XlYJE6P47oHVK99C0=");
        PayTRTokens.Notification(Salt, Salt, "ABC123", "success", "100").ShouldBe("6dDSyMFnQ6eANVo43BWHrgsfCj6vVoI9hyf0XMCWGP4=");
        PayTRTokens.Notification(Salt, Salt, "ABC123", "failed", "100").ShouldBe("0msy9HV2QtQBPzwpUTnia335Di8nxKhHjXcGQ9eNV+c=");
    }

    [Theory]
    [Trait("Requirement", "PTR-14")]
    [InlineData("0", "Kart bakiyesi yetersiz", FailureCode.InsufficientFunds)]
    [InlineData("0", "Günlük işlem limiti aşıldı", FailureCode.LimitExceeded)]
    [InlineData("0", "Kartın son kullanma tarihi geçmiş", FailureCode.ExpiredCard)]
    [InlineData("0", "KAYIP KART", FailureCode.LostOrStolen)]
    [InlineData("0", "Geçersiz kart numarası", FailureCode.InvalidCard)]
    [InlineData("0", "Bankaya ulaşılamadı", FailureCode.ProviderUnavailable)]
    [InlineData("0", "İşlem onaylanmadı", FailureCode.CardDeclined)]
    [InlineData("2", null, FailureCode.AuthenticationFailed)]
    [InlineData("3", null, FailureCode.FraudSuspected)]
    [InlineData("6", null, FailureCode.Abandoned)]
    [InlineData("9", null, FailureCode.CardDeclined)]
    [InlineData("10", null, FailureCode.AuthenticationRequired)]
    [InlineData("99", null, FailureCode.InvalidRequest)]
    public void Failures_MapByCode_ThenByTheBanksMessage(string code, string? message, FailureCode failure) =>
        PayTRFailures.Map(code, message).ShouldBe(failure);

    [Fact]
    [Trait("Requirement", "PTR-6")]
    [Trait("Requirement", "PTR-7")]
    [Trait("Requirement", "PTR-15")]
    public async Task APayment_GetsATokenForPayTRsIFrame_WithTheExactFieldsSigned()
    {
        var handler = new RecordedExchangeHandler("paytr/get-token.json");
        var context = Context(handler);

        var started = await _paytr.StartAsync(context, Start(), CancellationToken);
        var refused = await _paytr.StartAsync(context, Start(), CancellationToken);

        handler.AssertComplete();
        var sent = FormEncoding.Decode(handler.Requests[0].Body);
        var basket = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(sent["user_basket"])))!;
        basket.ToJsonString().ShouldBe(new JsonArray(new JsonArray("Pro (Aylık)", "299.00", 1)).ToJsonString());
        sent["paytr_token"].ShouldBe(PayTRTokens.IFrame(
            Key, Salt, "123456", "85.34.78.112", Reference, "ayse.yilmaz@example.com", "29900", sent["user_basket"], "1", "0", "TL", "1"));
        var embed = started.Presentation.ShouldBeOfType<Presentation.Embed>();
        embed.Html.ShouldContain("""<iframe src="https://www.paytr.com/odeme/guvenli/7f3c1a2b9d" id="paytriframe" """);
        embed.Html.ShouldContain("""<script nonce="{nonce}">iFrameResize(""");
        started.ExpiresAt.ShouldBe(_time.GetUtcNow().AddMinutes(30));
        (refused.Failure!.Status, refused.Failure.FailureCode).ShouldBe((PaymentStatus.Failed, FailureCode.InvalidRequest));
    }

    [Fact]
    [Trait("Requirement", "PTR-10")]
    [Trait("Requirement", "PTR-12")]
    public async Task TheStatusQuery_CompletesSuccessfulPayments_AndFindsOnlyThem()
    {
        var handler = new RecordedExchangeHandler("paytr/status.json");
        var context = Context(handler);

        var completed = await _paytr.CompleteAsync(context, new CompleteRequest { PaymentId = Guid.Empty, Reference = Reference, Amount = Amount }, CancellationToken);
        var pending = await _paytr.CompleteAsync(context, new CompleteRequest { PaymentId = Guid.Empty, Reference = Reference, Amount = Amount }, CancellationToken);
        var refund = await _paytr.FindRefundAsync(context, new RefundQuery(RefundReference, Reference, null, Reference, _time.GetUtcNow()), CancellationToken);
        var test = await _paytr.TestConnectionAsync(context, CancellationToken);

        handler.AssertComplete();
        (completed.Status, completed.ProviderPaymentId, completed.ChargedAmount).ShouldBe((PaymentStatus.Succeeded, Reference, Amount), "299,00 is ₺299.00");
        pending.Status.ShouldBe(PaymentStatus.Pending, "not paid yet: the notification or reconciliation decides");
        refund.ShouldBeOfType<RefundLookup.Found>().Outcome.Status.ShouldBe(RefundStatus.Succeeded);
        (test.Succeeded, test.FailureCode).ShouldBe((false, "invalid_credentials"));
    }

    [Fact]
    [Trait("Requirement", "PTR-13")]
    public async Task Refunds_SucceedWaitForTheNotificationOrFail()
    {
        var handler = new RecordedExchangeHandler("paytr/refunds.json");
        var context = Context(handler);

        var outcomes = new List<RefundOutcome>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            outcomes.Add(await _paytr.RefundAsync(context, Refund(), CancellationToken));
        }

        handler.AssertComplete();
        outcomes.Select(outcome => (outcome.Status, outcome.FailureCode)).ShouldBe(
        [
            (RefundStatus.Succeeded, null),
            (RefundStatus.Pending, "007"),
            (RefundStatus.Failed, "invalid_request"),
            (RefundStatus.Failed, "provider_unavailable"),
            (RefundStatus.Unknown, null),
        ]);
    }

    [Fact]
    [Trait("Requirement", "PTR-14")]
    public async Task Notifications_AreVerified_AnsweredOK_AndSuccessesConfirmedByTheStatusQuery()
    {
        var context = Context(new RecordedExchangeHandler(new RecordedExchanges("none", [])));

        var success = await _paytr.HandleNotificationAsync(context, Notification(Reference, "success", "29900"), CancellationToken);
        var failed = await _paytr.HandleNotificationAsync(context, Notification(Reference, "failed", "29900", ("failed_reason_code", "0"), ("failed_reason_msg", "Yetersiz bakiye")), CancellationToken);
        var abandoned = await _paytr.HandleNotificationAsync(context, Notification(Reference, "failed", "29900", ("failed_reason_code", "6")), CancellationToken);
        var unknown = await _paytr.HandleNotificationAsync(context, Notification("somebodyelse1", "success", "100"), CancellationToken);
        var forged = await _paytr.HandleNotificationAsync(context, Notification(Reference, "success", "29900", hash: "bm90IGEgaGFzaA=="), CancellationToken);

        (success.Verified, success.ProviderEventId, success.Response).ShouldBe((true, $"{Reference}:success", new NotificationResponse(200, "text/plain", "OK")));
        success.Updates.ShouldBe([new NotificationUpdate.CheckPayment(Reference)]);
        var outcome = failed.Updates.ShouldHaveSingleItem().ShouldBeOfType<NotificationUpdate.Payment>().Outcome;
        (outcome.Status, outcome.FailureCode, outcome.ProviderFailureMessage).ShouldBe((PaymentStatus.Failed, FailureCode.InsufficientFunds, "Yetersiz bakiye"));
        abandoned.Updates.ShouldHaveSingleItem().ShouldBeOfType<NotificationUpdate.Payment>().Outcome.FailureCode.ShouldBe(FailureCode.Abandoned);
        (unknown.Verified, unknown.Response.Body).ShouldBe((true, "OK"), "a store shared with another system sends its notifications too");
        (forged.Verified, forged.Response.Status).ShouldBe((false, 400));
    }

    [Fact]
    [Trait("Requirement", "PTR-2")]
    [Trait("Requirement", "PTR-9")]
    public async Task DirectFormMode_ShowsACardFormThatPostsToPayTR_AndChangesWhatTheConnectionCanDo()
    {
        var context = Context(new RecordedExchangeHandler(new RecordedExchanges("none", [])), direct: true);

        var started = await _paytr.StartAsync(context, Start() with { SaveCard = true, ProviderCustomer = "ut-77" }, CancellationToken);

        var form = started.Presentation.ShouldBeOfType<Presentation.DirectForm>();
        form.Action.ShouldBe(new Uri("https://www.paytr.com/odeme"));
        form.CardFields.ShouldBe(["cc_owner", "card_number", "expiry_month", "expiry_year", "cvv"]);
        (form.HiddenFields["payment_amount"], form.HiddenFields["non_3d"], form.HiddenFields["store_card"], form.HiddenFields["utoken"]).ShouldBe(("299.00", "0", "1", "ut-77"));
        form.HiddenFields["merchant_ok_url"].ShouldEndWith("?result=ok");
        form.HiddenFields["paytr_token"].ShouldBe(PayTRTokens.Direct(Key, Salt, "123456", "85.34.78.112", Reference, "ayse.yilmaz@example.com", "299.00", "card", "0", "TL", "1", "0"));
        form.HiddenFields.Keys.ShouldNotContain("merchant_key");
        form.HiddenFields.Keys.ShouldNotContain("merchant_salt");
        var (iframe, direct) = (_paytr.CapabilitiesFor(new Dictionary<string, string>()), _paytr.CapabilitiesFor(new Dictionary<string, string> { ["allowDirectForm"] = "true" }));
        (iframe.Entry.Single(), iframe.SavesCards, iframe.ChargesSavedCards).ShouldBe((CardEntry.Embedded, CardSaving.None, false));
        (direct.Entry.Single(), direct.SavesCards, direct.ChargesSavedCards, direct.ConcurrentChargesPerCard).ShouldBe((CardEntry.DirectForm, CardSaving.DuringPayment, true, 1));
    }

    [Fact]
    [Trait("Requirement", "PTR-10")]
    [Trait("Requirement", "PTR-14")]
    public async Task AStoredCard_IsFoundAmongTheUsersCards_ByTheNotificationForTheFirstOne()
    {
        var handler = new RecordedExchangeHandler("paytr/direct-cards.json");
        var context = Context(handler, direct: true);
        var complete = new CompleteRequest { PaymentId = Guid.Empty, Reference = Reference, Amount = Amount, SaveCard = true };

        var known = await _paytr.CompleteAsync(context, complete with { ProviderCustomer = "ut-77" }, CancellationToken);
        var first = await _paytr.CompleteAsync(context, complete, CancellationToken);
        var notified = await _paytr.HandleNotificationAsync(context, Notification(Reference, "success", "29900", ("utoken", "ut-new")), CancellationToken);
        var later = await _paytr.HandleNotificationAsync(context, Notification(Reference, "success", "29900", ("utoken", "ut-new")), CancellationToken);
        var removed = await _paytr.RemoveSavedCardAsync(context, new SavedCardReference("ut-77", """{"utoken":"ut-77","ctoken":"ct-9f2"}"""), CancellationToken);
        var gone = await _paytr.RemoveSavedCardAsync(context, new SavedCardReference("ut-77", """{"utoken":"ut-77","ctoken":"ct-9f2"}"""), CancellationToken);

        handler.AssertComplete();
        known.SavedCard.ShouldBe(new SavedCard
        {
            ProviderCustomer = "ut-77",
            Token = """{"utoken":"ut-77","ctoken":"ct-9f2"}""",
            Last4 = "4358",
            ExpMonth = 12,
            ExpYear = 2030,
            Issuer = "Kuveyt Türk",
            Brand = "visa",
            Funding = "credit",
        });
        first.Status.ShouldBe(PaymentStatus.Pending, "PayTR names its user for a first card only in the notification");
        var outcome = notified.Updates.ShouldHaveSingleItem().ShouldBeOfType<NotificationUpdate.Payment>().Outcome;
        (outcome.Status, outcome.ChargedAmount, outcome.SavedCard!.ProviderCustomer, outcome.SavedCard.Token).ShouldBe(
            (PaymentStatus.Succeeded, Amount, "ut-new", """{"utoken":"ut-new","ctoken":"ct-first"}"""));
        notified.Response.Body.ShouldBe("OK");
        (later.Deferred, later.Response.Status).ShouldBe((true, 503), "not answered OK, so PayTR notifies again");
        (removed.Removed, gone.Removed).ShouldBe((true, true));
    }

    [Fact]
    [Trait("Requirement", "PTR-11")]
    public async Task RecurringCharges_Succeed_WaitOrFailWithTheirReasons()
    {
        var handler = new RecordedExchangeHandler("paytr/recurring.json");
        var context = Context(handler, direct: true);
        var charge = new ChargeRequest
        {
            PaymentId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a3"),
            Reference = "0199c0de0000700080000000000000a3",
            Amount = Amount,
            SavedCard = new SavedCardReference("ut-77", """{"utoken":"ut-77","ctoken":"ct-9f2"}"""),
            Customer = new CustomerInfo { Id = Guid.Empty, Name = "Ayşe Yılmaz", Email = "ayşe.yılmaz@example.com", IpAddress = "85.34.78.112" },
            Description = "Pro (Aylık), yenileme",
        };

        var outcomes = new List<PaymentOutcome>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            outcomes.Add(await _paytr.ChargeSavedCardAsync(context, charge, CancellationToken));
        }

        handler.AssertComplete();
        outcomes.Select(outcome => (outcome.Status, outcome.FailureCode)).ShouldBe(
        [
            (PaymentStatus.Succeeded, null),
            (PaymentStatus.Pending, null),
            (PaymentStatus.Failed, FailureCode.InsufficientFunds),
            (PaymentStatus.Failed, FailureCode.ProviderUnavailable),
            (PaymentStatus.Failed, FailureCode.MerchantNotPermitted),
            (PaymentStatus.Unknown, null),
        ]);
        var sent = FormEncoding.Decode(handler.Requests[0].Body);
        sent["paytr_token"].ShouldBe(PayTRTokens.Direct(Key, Salt, "123456", "85.34.78.112", charge.Reference, "ayse.yilmaz@example.com", "299.00", "card", "0", "TL", "1", "1"));
    }

    private static StartRequest Start() => new()
    {
        PaymentId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a1"),
        Reference = Reference,
        Amount = Amount,
        Purpose = PaymentPurpose.Charge,
        Customer = new CustomerInfo
        {
            Id = Guid.Parse("0199c0de-0000-7000-8000-0000000000c1"),
            Name = "Ayşe Yılmaz",
            Email = "ayşe.yılmaz@example.com",
            Phone = "05321234567",
            AddressLine = "Moda Cad. 1, Kadıköy",
            IpAddress = "85.34.78.112",
        },
        Description = "Pro (Aylık)",
        ReturnUrl = new Uri("https://billing.example.com/checkout/cs_x/return/0199c0de-0000-7000-8000-0000000000a1"),
        CancelUrl = new Uri("https://billing.example.com/checkout/cs_x"),
        Locale = "tr",
    };

    private RefundRequest Refund() => new()
    {
        RefundId = Guid.Parse("0199c0de-0000-7000-8000-0000000000b1"),
        Reference = RefundReference,
        PaymentReference = Reference,
        ProviderPaymentId = Reference,
        PaymentAmount = Amount,
        PaymentSucceededAt = _time.GetUtcNow(),
        Amount = Amount with { MinorUnits = 10_000 },
    };

    /// <summary>A notification as PayTR posts it, signed with the connection's key and salt unless a hash is given.</summary>
    private static IncomingNotification Notification(string reference, string status, string total, params (string Name, string Value)[] extra) =>
        Notification(reference, status, total, null, extra);

    private static IncomingNotification Notification(string reference, string status, string total, string? hash, params (string Name, string Value)[] extra)
    {
        var fields = new Dictionary<string, string>
        {
            ["merchant_oid"] = reference,
            ["status"] = status,
            ["total_amount"] = total,
            ["hash"] = hash ?? PayTRTokens.Notification(Key, Salt, reference, status, total),
            ["payment_type"] = "card",
            ["currency"] = "TL",
        };
        foreach (var (name, value) in extra)
        {
            fields[name] = value;
        }

        return new IncomingNotification { Method = "POST", Headers = new Dictionary<string, string>(), Body = Encoding.UTF8.GetBytes(FormEncoding.Encode(fields)) };
    }

    private ProviderContext Context(RecordedExchangeHandler handler, bool direct = false) => new()
    {
        ConnectionId = Guid.Parse("0199c0de-0000-7000-8000-00000000c002"),
        Environment = ProviderEnvironment.Sandbox,
        Credentials = new Dictionary<string, string> { ["merchantId"] = "123456", ["merchantKey"] = Key, ["merchantSalt"] = Salt },
        Settings = direct ? new Dictionary<string, string> { ["allowDirectForm"] = "true" } : new Dictionary<string, string>(),
        Http = handler.Client(),
        Time = _time,
        Logger = NullLogger.Instance,
        NotificationUrl = new Uri("https://billing.example.com/provider-notifications/0199c0de-0000-7000-8000-00000000c002"),
    };
}
