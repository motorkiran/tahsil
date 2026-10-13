using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.PayTR;

/// <summary>
/// PayTR (docs/specs/providers/paytr.md). In iFrame mode, the default, customers pay in PayTR's iframe and nothing is
/// stored, so a subscription is paid invoice by invoice (PTR-3); payments are confirmed through the status query and
/// signed notifications, which are answered <c>OK</c>.
/// </summary>
public sealed class PayTRProvider : IPaymentProvider
{
    public const string Key = "paytr";

    private const string MerchantId = "merchantId";
    private const string MerchantKey = "merchantKey";
    private const string MerchantSalt = "merchantSalt";
    private const string AllowDirectForm = "allowDirectForm";
    private const string FallbackAddress = "fallbackAddress";
    private const string FallbackPhone = "fallbackPhone";

    /// <summary>How long PayTR keeps a payment open (<c>timeout_limit</c>, in minutes).</summary>
    private const int TimeoutLimit = 30;

    private const int Exponent = 2;

    private static readonly TimeSpan TokenTimeout = TimeSpan.FromSeconds(20);
    private static readonly string[] Currencies = ["TRY", "USD", "EUR"];
    private static readonly string[] PayTROrigin = ["https://www.paytr.com"];

    private static readonly ProviderCapabilities IFrameMode = new()
    {
        Entry = new HashSet<CardEntry> { CardEntry.Embedded },
        SavesCards = CardSaving.None,
        Setup = CardSetup.None,
        ChargesSavedCards = false,
        Refunds = RefundSupport.Partial,
        Voids = VoidSupport.None,
        FindByReference = ReferenceSearch.SuccessfulOnly,
        Notifications = NotificationSupport.Verifiable,
    };

    public ProviderDescriptor Descriptor { get; } = new()
    {
        Key = Key,
        DisplayName = "PayTR",
        Credentials =
        [
            new FieldDefinition { Key = MerchantId, Label = "Merchant ID (Mağaza No)", Type = FieldType.Text, Pattern = "^[0-9]+$" },
            new FieldDefinition { Key = MerchantKey, Label = "Merchant key (Mağaza Parola)", Type = FieldType.Secret },
            new FieldDefinition { Key = MerchantSalt, Label = "Merchant salt (Mağaza Gizli Anahtar)", Type = FieldType.Secret },
        ],
        Settings =
        [
            new FieldDefinition
            {
                Key = AllowDirectForm,
                Label = "Direct form mode",
                Type = FieldType.Boolean,
                Required = false,
                Default = "false",
                Description = "Card fields on Tahsil's page posting straight to PayTR, so that cards are stored and renewals charged (PCI DSS SAQ A-EP; needs PayTR's Direkt API, Non3D and recurring payment permissions).",
            },
            new FieldDefinition { Key = FallbackAddress, Label = "Address for customers without one", Required = false, Description = "PayTR requires an address with every payment." },
            new FieldDefinition { Key = FallbackPhone, Label = "Phone for customers without one", Required = false, Description = "PayTR requires a phone number with every payment." },
        ],
        Currencies = new HashSet<string>(Currencies, StringComparer.Ordinal),
        MinimumAmounts = Currencies.ToDictionary(currency => currency, _ => 100L, StringComparer.Ordinal),
        Capabilities = IFrameMode,
        CustomerData = new CustomerDataRequirements(
            [
                new(CustomerField.Name, null),
                new(CustomerField.Email, null),
                new(CustomerField.Address, DefaultAddress),
                new(CustomerField.Phone, DefaultPhone),
                new(CustomerField.IpAddress, DefaultIp),
            ],
            []),
        ContentSecurity = new Dictionary<ProviderEnvironment, ContentSecuritySources>
        {
            [ProviderEnvironment.Sandbox] = Sources,
            [ProviderEnvironment.Production] = Sources,
        },
    };

    private static string DefaultAddress => "Türkiye";

    private static string DefaultPhone => "05000000000";

    private static string DefaultIp => "127.0.0.1";

    /// <summary>The iframe and its resizer come from PayTR, which also loads Tahsil's return pages inside the iframe (PTR-8).</summary>
    private static ContentSecuritySources Sources => new() { Scripts = PayTROrigin, Frames = PayTROrigin, ReturnFrameAncestors = PayTROrigin };

    /// <summary>A status query for a reference that cannot exist: "not found" proves the keys (§3.2).</summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        using var answer = await StatusAsync(context, $"tahsiltest{Guid.CreateVersion7():N}", cancellationToken);
        return answer switch
        {
            { Unanswered: true } => new ConnectionTestResult(false, "unreachable", "PayTR did not answer; try again later."),
            { ErrorNumber: "004" } => ConnectionTestResult.Success,
            { ErrorNumber: "003" } => new ConnectionTestResult(false, "invalid_credentials", "PayTR refused the token: check the merchant key and salt."),
            { ErrorNumber: "001" } => new ConnectionTestResult(false, "invalid_credentials", "PayTR refused the request: check the merchant ID, or the store is not active."),
            _ => new ConnectionTestResult(false, answer.ErrorNumber ?? "provider_error", answer.Message),
        };
    }

    /// <summary>A token for PayTR's iframe, embedded with its resizer (PTR-7).</summary>
    public async Task<StartResult> StartAsync(ProviderContext context, StartRequest request, CancellationToken cancellationToken)
    {
        var merchant = context.Credential(MerchantId);
        var sandbox = context.Environment == ProviderEnvironment.Sandbox ? "1" : "0";
        var userIp = request.Customer.IpAddress ?? DefaultIp;
        var email = Email(request.Customer.Email ?? string.Empty);
        var amount = request.Amount.MinorUnits.ToString(CultureInfo.InvariantCulture);
        var description = request.Description ?? (request.Lines.Count > 0 ? request.Lines[0].Description : "Ödeme");
        var basket = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonArray(new JsonArray(description, AmountFormat.Fixed(request.Amount), 1)).ToJsonString()));
        var currency = Currency(request.Amount.Currency);
        var token = PayTRTokens.IFrame(
            context.Credential(MerchantKey), context.Credential(MerchantSalt), merchant, userIp, request.Reference, email, amount, basket, "1", "0", currency, sandbox);

        using var answer = await PayTRApi.PostAsync(context, "odeme/api/get-token",
        [
            new("merchant_id", merchant),
            new("user_ip", userIp),
            new("merchant_oid", request.Reference),
            new("email", email),
            new("payment_amount", amount),
            new("paytr_token", token),
            new("user_basket", basket),
            new("debug_on", sandbox),
            new("no_installment", "1"),
            new("max_installment", "0"),
            new("user_name", Text(request.Customer.Name ?? email, 60)),
            new("user_address", Text(request.Customer.AddressLine ?? context.Setting(FallbackAddress) ?? DefaultAddress, 400)),
            new("user_phone", Text(request.Customer.Phone ?? context.Setting(FallbackPhone) ?? DefaultPhone, 20)),
            new("merchant_ok_url", Result(request.ReturnUrl, "ok")),
            new("merchant_fail_url", Result(request.ReturnUrl, "fail")),
            new("timeout_limit", TimeoutLimit.ToString(CultureInfo.InvariantCulture)),
            new("currency", currency),
            new("test_mode", sandbox),
            new("lang", request.Locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en"),
        ], TokenTimeout, cancellationToken);

        if (answer.Unanswered || !answer.Succeeded || answer.Root.TextAt("token") is not { Length: > 0 } iframeToken)
        {
            // A token charges nothing; the customer may try again.
            return StartResult.Failed(PaymentOutcome.Failure(
                answer.Unanswered ? FailureCode.ProviderUnavailable : FailureCode.InvalidRequest, answer.ErrorNumber, answer.Message ?? "PayTR did not issue a token."));
        }

        var frame = WebUtility.HtmlEncode($"https://www.paytr.com/odeme/guvenli/{iframeToken}");
        return new StartResult
        {
            Presentation = new Presentation.Embed(
                $$"""
                <script src="https://www.paytr.com/js/iframeResizer.min.js" nonce="{nonce}"></script>
                <iframe src="{{frame}}" id="paytriframe" class="provider-frame" frameborder="0" scrolling="no" style="width: 100%;" title="PayTR"></iframe>
                <script nonce="{nonce}">iFrameResize({ log: false, checkOrigin: ['https://www.paytr.com'] }, '#paytriframe');</script>
                """,
                []),
            ExpiresAt = context.Time.GetUtcNow().AddMinutes(TimeoutLimit),
        };
    }

    /// <summary>The OK URL carries nothing PayTR signed: the status query says whether the payment succeeded (PTR-10).</summary>
    public async Task<PaymentOutcome> CompleteAsync(ProviderContext context, CompleteRequest request, CancellationToken cancellationToken)
    {
        using var answer = await StatusAsync(context, request.Reference, cancellationToken);
        return answer switch
        {
            { Unanswered: true } => PaymentOutcome.Unknown("PayTR did not answer the status query."),
            { Succeeded: true } => Paid(answer, request.Reference),

            // Not paid yet: the signed notification decides, or reconciliation after the time limit (PAY-16).
            _ => new PaymentOutcome { Status = PaymentStatus.Pending },
        };
    }

    /// <summary>An iFrame mode connection stores no cards, so the engine never asks it to charge one.</summary>
    public Task<PaymentOutcome> ChargeSavedCardAsync(ProviderContext context, ChargeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentOutcome.Failure(FailureCode.MerchantNotPermitted, providerMessage: "This PayTR connection stores no cards; enable direct form mode."));

    /// <summary>The status query reports successful payments only (PTR-12).</summary>
    public async Task<PaymentLookup> FindPaymentAsync(ProviderContext context, PaymentQuery query, CancellationToken cancellationToken)
    {
        using var answer = await StatusAsync(context, query.Reference, cancellationToken);
        return answer switch
        {
            { Unanswered: true } => new PaymentLookup.Unavailable("PayTR did not answer the status query."),
            { Succeeded: true } => new PaymentLookup.Found(Paid(answer, query.Reference)),
            { ErrorNumber: "004" } => new PaymentLookup.NotFound(),
            _ => new PaymentLookup.Unavailable(answer.Message),
        };
    }

    /// <summary>A refund of part or all of the payment, cumulative, within a year (PTR-13).</summary>
    public async Task<RefundOutcome> RefundAsync(ProviderContext context, RefundRequest request, CancellationToken cancellationToken)
    {
        var merchant = context.Credential(MerchantId);
        var amount = AmountFormat.Fixed(request.Amount);
        using var answer = await PayTRApi.PostAsync(context, "odeme/iade",
        [
            new("merchant_id", merchant),
            new("merchant_oid", request.PaymentReference),
            new("return_amount", amount),
            new("paytr_token", PayTRTokens.Refund(context.Credential(MerchantKey), context.Credential(MerchantSalt), merchant, request.PaymentReference, amount)),
            new("reference_no", request.Reference),
        ], ProviderHttp.ChangeTimeout, cancellationToken);

        return answer switch
        {
            { Unanswered: true } => RefundOutcome.Unknown("PayTR did not answer the refund."),
            { Succeeded: true } => new RefundOutcome { Status = RefundStatus.Succeeded },

            // The payment's notification is not acknowledged yet: reconciliation tries again.
            { ErrorNumber: "007" } => new RefundOutcome { Status = RefundStatus.Pending, FailureCode = "007", FailureMessage = answer.Message },
            { ErrorNumber: "000" } => Refused("provider_unavailable", answer.Message),
            { ErrorNumber: "010" or "BLK" } => Refused("provider_error", answer.Message),
            _ => Refused("invalid_request", answer.Message ?? $"PayTR refused the refund ({answer.ErrorNumber})."),
        };

        static RefundOutcome Refused(string code, string? message) => new() { Status = RefundStatus.Failed, FailureCode = code, FailureMessage = message };
    }

    /// <summary>The status query's returns, by Tahsil's refund reference (§3.8).</summary>
    public async Task<RefundLookup> FindRefundAsync(ProviderContext context, RefundQuery query, CancellationToken cancellationToken)
    {
        using var answer = await StatusAsync(context, query.PaymentReference, cancellationToken);
        if (answer.Unanswered || !answer.Succeeded)
        {
            return answer.ErrorNumber == "004" ? new RefundLookup.NotFound() : new RefundLookup.Unavailable(answer.Message);
        }

        return answer.Root.ArrayAt("returns").Any(entry => entry.TextAt("reference_no") == query.Reference)
            ? new RefundLookup.Found(new RefundOutcome { Status = RefundStatus.Succeeded })
            : new RefundLookup.NotFound();
    }

    /// <summary>An iFrame mode connection stores no cards.</summary>
    public Task<RemoveCardResult> RemoveSavedCardAsync(ProviderContext context, SavedCardReference card, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoveCardResult(true));

    /// <summary>
    /// A form post signed over the reference, status and total only (PTR-14): a success is confirmed through the status
    /// query, a failure is taken as it is, and every valid notification, known or not, is answered <c>OK</c>.
    /// </summary>
    public Task<NotificationResult> HandleNotificationAsync(ProviderContext context, IncomingNotification notification, CancellationToken cancellationToken)
    {
        var form = FormEncoding.Decode(notification.Body);
        var reference = form.GetValueOrDefault("merchant_oid");
        var status = form.GetValueOrDefault("status");
        var total = form.GetValueOrDefault("total_amount");
        if (reference is null || status is null || total is null
            || !Hmac.Matches(PayTRTokens.Notification(context.Credential(MerchantKey), context.Credential(MerchantSalt), reference, status, total), form.GetValueOrDefault("hash")))
        {
            return Task.FromResult(NotificationResult.Rejected(new NotificationResponse(400, "text/plain", "invalid hash")));
        }

        NotificationUpdate update = status == "success"
            ? new NotificationUpdate.CheckPayment(reference)
            : new NotificationUpdate.Payment(reference, PaymentOutcome.Failure(
                PayTRFailures.Map(form.GetValueOrDefault("failed_reason_code"), form.GetValueOrDefault("failed_reason_msg")),
                form.GetValueOrDefault("failed_reason_code"),
                form.GetValueOrDefault("failed_reason_msg")));
        return Task.FromResult(new NotificationResult
        {
            Verified = true,
            ProviderEventId = $"{reference}:{status}",
            EventType = status,
            Response = new NotificationResponse(200, "text/plain", "OK"),
            Updates = [update],
        });
    }

    /// <summary>The capabilities of the connection's mode (PTR-2).</summary>
    public ProviderCapabilities CapabilitiesFor(IReadOnlyDictionary<string, string> settings) => IFrameMode;

    private static Task<PayTRAnswer> StatusAsync(ProviderContext context, string reference, CancellationToken cancellationToken)
    {
        var merchant = context.Credential(MerchantId);
        return PayTRApi.PostAsync(context, "odeme/durum-sorgu",
        [
            new("merchant_id", merchant),
            new("merchant_oid", reference),
            new("paytr_token", PayTRTokens.Status(context.Credential(MerchantKey), context.Credential(MerchantSalt), merchant, reference)),
        ], ProviderHttp.ChangeTimeout, cancellationToken);
    }

    /// <summary>A successful status: what PayTR charged, which the engine compares with the payment (PAY-9).</summary>
    private static PaymentOutcome Paid(PayTRAnswer answer, string reference)
    {
        var charged = Decimal(answer.Root.TextAt("payment_amount"));
        var currency = answer.Root.TextAt("currency") is "TL" or null ? "TRY" : answer.Root.TextAt("currency")!;
        return new PaymentOutcome
        {
            Status = PaymentStatus.Succeeded,
            ProviderPaymentId = reference,
            ChargedAmount = charged is { } value && AmountFormat.ToMinorUnits(value, Exponent) is { } minor ? new Amount(minor, currency, Exponent) : null,
        };
    }

    /// <summary>A decimal string, possibly with a comma (<c>10,8</c>), as the status query writes them (PTR-15).</summary>
    private static decimal? Decimal(string? text) =>
        decimal.TryParse(text?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Currency(string currency) => currency == "TRY" ? "TL" : currency;

    private static string Result(Uri returnUrl, string result) =>
        $"{returnUrl.AbsoluteUri}{(string.IsNullOrEmpty(returnUrl.Query) ? '?' : '&')}result={result}";

    /// <summary>Within PayTR's limits (PTR-6).</summary>
    private static string Text(string value, int maxLength) => value.Length > maxLength ? value[..maxLength] : value;

    /// <summary>Without Turkish letters, which PayTR refuses in e-mail addresses (PTR-6).</summary>
    private static string Email(string value)
    {
        var plain = new StringBuilder(value.Length);
        foreach (var letter in value)
        {
            plain.Append(letter switch
            {
                'ç' => 'c',
                'Ç' => 'C',
                'ğ' => 'g',
                'Ğ' => 'G',
                'ı' => 'i',
                'İ' => 'I',
                'ö' => 'o',
                'Ö' => 'O',
                'ş' => 's',
                'Ş' => 'S',
                'ü' => 'u',
                'Ü' => 'U',
                _ => letter,
            });
        }

        return Text(plain.ToString(), 100);
    }
}
