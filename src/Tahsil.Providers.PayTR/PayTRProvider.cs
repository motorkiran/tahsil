using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.PayTR;

/// <summary>
/// PayTR (docs/specs/providers/paytr.md). In iFrame mode, the default, customers pay in PayTR's iframe and nothing is
/// stored, so a subscription is paid invoice by invoice (PTR-3). In direct form mode (<c>allowDirectForm</c>, PCI DSS
/// SAQ A-EP) the card fields post from Tahsil's page straight to PayTR, which stores the card and later charges it
/// without 3D Secure. Either way, payments are confirmed through the status query and signed notifications, which are
/// answered <c>OK</c>.
/// </summary>
public sealed class PayTRProvider : IPaymentProvider
{
    public const string Key = "paytr";

    private const string MerchantId = "merchantId";
    private const string MerchantKey = "merchantKey";
    private const string MerchantSalt = "merchantSalt";
    private const string AllowDirectForm = "allowDirectForm";
    private const string FallbackAddress = "fallbackAddress";
    private const string UserToken = "utoken";
    private const string CardToken = "ctoken";
    private const string FallbackPhone = "fallbackPhone";

    /// <summary>How long PayTR keeps a payment open (<c>timeout_limit</c>, in minutes).</summary>
    private const int TimeoutLimit = 30;

    private const int Exponent = 2;

    private static readonly TimeSpan TokenTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RecurringTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] CardFields = ["cc_owner", "card_number", "expiry_month", "expiry_year", "cvv"];
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

    private static readonly ProviderCapabilities DirectFormMode = new()
    {
        Entry = new HashSet<CardEntry> { CardEntry.DirectForm },
        SavesCards = CardSaving.DuringPayment,
        Setup = CardSetup.VerificationCharge,
        ChargesSavedCards = true,
        ChargingSavedCardsRequiresMerchantPermission = true,
        Refunds = RefundSupport.Partial,
        Voids = VoidSupport.None,
        FindByReference = ReferenceSearch.SuccessfulOnly,
        Notifications = NotificationSupport.Verifiable,
        ConcurrentChargesPerCard = 1,
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
            new FieldDefinition { Key = FallbackAddress, Label = "Address for customers without one", Required = false, Description = "PayTR requires an address with every payment." },
            new FieldDefinition { Key = FallbackPhone, Label = "Phone for customers without one", Required = false, Description = "PayTR requires a phone number with every payment." },
        ],
        Currencies = new HashSet<string>(Currencies, StringComparer.Ordinal),
        MinimumAmounts = Currencies.ToDictionary(currency => currency, _ => 100L, StringComparer.Ordinal),

        // Both ways of taking cards; a connection has one of them, by its allowDirectForm setting (PTR-2).
        Capabilities = IFrameMode with { Entry = new HashSet<CardEntry> { CardEntry.Embedded, CardEntry.DirectForm } },
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
    private static ContentSecuritySources Sources => new() { Scripts = PayTROrigin, Frames = PayTROrigin, FormActions = PayTROrigin, ReturnFrameAncestors = PayTROrigin };

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
        if (IsDirect(context.Settings))
        {
            return DirectForm(context, request);
        }

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
        if (request.SaveCard && request.ProviderCustomer is null && IsDirect(context.Settings))
        {
            // The first card: PayTR names its user (utoken) only in the notification, which settles the payment with the card.
            return new PaymentOutcome { Status = PaymentStatus.Pending };
        }

        using var answer = await StatusAsync(context, request.Reference, cancellationToken);
        if (answer.Unanswered)
        {
            return PaymentOutcome.Unknown("PayTR did not answer the status query.");
        }

        if (!answer.Succeeded)
        {
            // Not paid yet: the signed notification decides, or reconciliation after the time limit (PAY-16).
            return new PaymentOutcome { Status = PaymentStatus.Pending };
        }

        var paid = Paid(answer, request.Reference);
        return request.SaveCard && request.ProviderCustomer is { } utoken
            ? paid with { SavedCard = await StoredCardAsync(context, utoken, answer.Root.TextAt("masked_pan"), cancellationToken) }
            : paid;
    }

    /// <summary>
    /// A recurring charge of the stored card, server to server and without 3D Secure (PTR-11). PayTR's pages name the
    /// flag <c>recurring_payment</c> and its Postman collection <c>recurring</c>; both are sent.
    /// </summary>
    public async Task<PaymentOutcome> ChargeSavedCardAsync(ProviderContext context, ChargeRequest request, CancellationToken cancellationToken)
    {
        if (!IsDirect(context.Settings) || Card(request.SavedCard.Token) is not { } card)
        {
            return PaymentOutcome.Failure(FailureCode.MerchantNotPermitted, providerMessage: "This PayTR connection stores no cards; enable direct form mode.");
        }

        var fields = DirectFields(context, request.Reference, request.Amount, request.Customer, request.Description, "tr", non3d: "1", context.NotificationUrl, context.NotificationUrl);
        fields.Add(new(UserToken, card.UserToken));
        fields.Add(new(CardToken, card.CardToken));
        fields.Add(new("recurring_payment", "1"));
        fields.Add(new("recurring", "1"));
        using var answer = await PayTRApi.PostAsync(context, "odeme", fields, RecurringTimeout, cancellationToken);
        return answer switch
        {
            { Unanswered: true } => PaymentOutcome.Unknown("PayTR did not answer the recurring charge."),
            { Status: "success" } => new PaymentOutcome { Status = PaymentStatus.Succeeded, ProviderPaymentId = request.Reference, ChargedAmount = request.Amount },
            { Status: "wait_callback" } => new PaymentOutcome { Status = PaymentStatus.Pending, ProviderPaymentId = request.Reference },

            // Another charge of the card is in progress: nothing was charged.
            _ when answer.Root.ValueKind == System.Text.Json.JsonValueKind.Object && answer.Root.BooleanAt("try_again") == true =>
                PaymentOutcome.Failure(FailureCode.ProviderUnavailable, providerMessage: answer.Message),
            _ when answer.Message?.Contains("yetki", StringComparison.OrdinalIgnoreCase) == true =>
                PaymentOutcome.Failure(FailureCode.MerchantNotPermitted, providerMessage: answer.Message),
            _ => PaymentOutcome.Failure(PayTRFailures.ByMessage(answer.Message), providerMessage: answer.Message),
        };
    }

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

    /// <summary>Deletes the stored card (§3.9); one PayTR no longer has counts as removed.</summary>
    public async Task<RemoveCardResult> RemoveSavedCardAsync(ProviderContext context, SavedCardReference card, CancellationToken cancellationToken)
    {
        if (Card(card.Token) is not { } stored)
        {
            return new RemoveCardResult(true);
        }

        using var answer = await PayTRApi.PostAsync(context, "odeme/capi/delete",
        [
            new("merchant_id", context.Credential(MerchantId)),
            new(CardToken, stored.CardToken),
            new(UserToken, stored.UserToken),
            new("paytr_token", PayTRTokens.CardDelete(context.Credential(MerchantKey), context.Credential(MerchantSalt), stored.CardToken, stored.UserToken)),
        ], TokenTimeout, cancellationToken);
        return answer.Succeeded || answer.Message?.Contains("Kart yok", StringComparison.OrdinalIgnoreCase) == true
            ? new RemoveCardResult(true)
            : new RemoveCardResult(false, answer.Unanswered ? "PayTR did not answer." : answer.Message);
    }

    /// <summary>
    /// A form post signed over the reference, status and total only (PTR-14): a success is confirmed through the status
    /// query, a failure is taken as it is, and every valid notification, known or not, is answered <c>OK</c>.
    /// </summary>
    public async Task<NotificationResult> HandleNotificationAsync(ProviderContext context, IncomingNotification notification, CancellationToken cancellationToken)
    {
        var form = FormEncoding.Decode(notification.Body);
        var reference = form.GetValueOrDefault("merchant_oid");
        var status = form.GetValueOrDefault("status");
        var total = form.GetValueOrDefault("total_amount");
        if (reference is null || status is null || total is null
            || !Hmac.Matches(PayTRTokens.Notification(context.Credential(MerchantKey), context.Credential(MerchantSalt), reference, status, total), form.GetValueOrDefault("hash")))
        {
            return NotificationResult.Rejected(new NotificationResponse(400, "text/plain", "invalid hash"));
        }

        var result = new NotificationResult { Verified = true, ProviderEventId = $"{reference}:{status}", EventType = status, Response = new NotificationResponse(200, "text/plain", "OK") };
        if (status != "success")
        {
            return result with
            {
                Updates =
                [
                    new NotificationUpdate.Payment(reference, PaymentOutcome.Failure(
                        PayTRFailures.Map(form.GetValueOrDefault("failed_reason_code"), form.GetValueOrDefault("failed_reason_msg")),
                        form.GetValueOrDefault("failed_reason_code"),
                        form.GetValueOrDefault("failed_reason_msg"))),
                ],
            };
        }

        if (form.GetValueOrDefault(UserToken) is not { Length: > 0 } utoken)
        {
            return result with { Updates = [new NotificationUpdate.CheckPayment(reference)] };
        }

        // A card was stored: the payment is confirmed through the status query, whose masked number names the card among
        // the user's stored ones. When PayTR cannot be asked now, it is not answered OK, so that it notifies again.
        using var paid = await StatusAsync(context, reference, cancellationToken);
        if (paid.Unanswered)
        {
            return result with { Deferred = true, Response = new NotificationResponse(503, "text/plain", "try again") };
        }

        if (!paid.Succeeded)
        {
            return result with { Updates = [new NotificationUpdate.CheckPayment(reference)] };
        }

        var outcome = Paid(paid, reference) with { SavedCard = await StoredCardAsync(context, utoken, paid.Root.TextAt("masked_pan"), cancellationToken) };
        return result with { Updates = [new NotificationUpdate.Payment(reference, outcome)] };
    }

    /// <summary>The capabilities of the connection's mode (PTR-2).</summary>
    public ProviderCapabilities CapabilitiesFor(IReadOnlyDictionary<string, string> settings) => IsDirect(settings) ? DirectFormMode : IFrameMode;

    private static bool IsDirect(IReadOnlyDictionary<string, string> settings) => settings.GetValueOrDefault(AllowDirectForm) == "true";

    /// <summary>The form Tahsil shows on a page of its own: hidden fields with their token, and PayTR's card fields, posted to PayTR only (PTR-9).</summary>
    private static StartResult DirectForm(ProviderContext context, StartRequest request)
    {
        var fields = DirectFields(
            context, request.Reference, request.Amount, request.Customer, request.Description ?? (request.Lines.Count > 0 ? request.Lines[0].Description : null), request.Locale,
            non3d: "0", new Uri(Result(request.ReturnUrl, "ok")), new Uri(Result(request.ReturnUrl, "fail")));
        if (request.SaveCard)
        {
            fields.Add(new("store_card", "1"));
        }

        if (request.ProviderCustomer is { } utoken)
        {
            // Every card of the customer goes to one PayTR user.
            fields.Add(new(UserToken, utoken));
        }

        return new StartResult
        {
            Presentation = new Presentation.DirectForm(new Uri(PayTRApi.Host, "odeme"), fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal), CardFields),
            ExpiresAt = context.Time.GetUtcNow().AddMinutes(TimeoutLimit),
        };
    }

    /// <summary>The fields of a direct payment, with or without the customer (PTR-9, PTR-11): the amount is a decimal here (PTR-15).</summary>
    private static List<KeyValuePair<string, string>> DirectFields(
        ProviderContext context, string reference, Amount amount, CustomerInfo customer, string? description, string locale, string non3d, Uri okUrl, Uri failUrl)
    {
        var merchant = context.Credential(MerchantId);
        var sandbox = context.Environment == ProviderEnvironment.Sandbox ? "1" : "0";
        var userIp = customer.IpAddress ?? DefaultIp;
        var email = Email(customer.Email ?? string.Empty);
        var price = AmountFormat.Fixed(amount);
        var currency = Currency(amount.Currency);
        return
        [
            new("merchant_id", merchant),
            new("user_ip", userIp),
            new("merchant_oid", reference),
            new("email", email),
            new("payment_type", "card"),
            new("payment_amount", price),
            new("installment_count", "0"),
            new("currency", currency),
            new("test_mode", sandbox),
            new("non_3d", non3d),
            new("client_lang", locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en"),
            new("merchant_ok_url", okUrl.AbsoluteUri),
            new("merchant_fail_url", failUrl.AbsoluteUri),
            new("user_name", Text(customer.Name ?? email, 60)),
            new("user_address", Text(customer.AddressLine ?? context.Setting(FallbackAddress) ?? DefaultAddress, 400)),
            new("user_phone", Text(customer.Phone ?? context.Setting(FallbackPhone) ?? DefaultPhone, 20)),
            new("user_basket", new JsonArray(new JsonArray(description ?? "Ödeme", price, 1)).ToJsonString()),
            new("debug_on", sandbox),
            new("paytr_token", PayTRTokens.Direct(
                context.Credential(MerchantKey), context.Credential(MerchantSalt), merchant, userIp, reference, email, price, "card", "0", currency, sandbox, non3d)),
        ];
    }

    /// <summary>
    /// The card PayTR stored with a payment (PTR-10): among the user's cards, the one whose last four digits the status
    /// query shows, or the only one. Nothing when PayTR cannot tell, and the payment stands without a saved card.
    /// </summary>
    private static async Task<SavedCard?> StoredCardAsync(ProviderContext context, string utoken, string? maskedPan, CancellationToken cancellationToken)
    {
        using var answer = await PayTRApi.PostAsync(context, "odeme/capi/list",
        [
            new("merchant_id", context.Credential(MerchantId)),
            new(UserToken, utoken),
            new("paytr_token", PayTRTokens.CardList(context.Credential(MerchantKey), context.Credential(MerchantSalt), utoken)),
        ], TokenTimeout, cancellationToken);
        if (answer.Unanswered || answer.Root.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        var cards = answer.Root.EnumerateArray().ToList();
        var last4 = maskedPan is { Length: >= 4 } ? maskedPan[^4..] : null;
        var matching = cards.Where(card => card.TextAt("last_4") == last4).ToList();
        if ((matching.Count == 1 ? matching[0] : cards.Count == 1 ? cards[0] : default) is not { ValueKind: System.Text.Json.JsonValueKind.Object } stored
            || stored.TextAt(CardToken) is not { } ctoken)
        {
            return null;
        }

        return new SavedCard
        {
            ProviderCustomer = utoken,
            Token = new JsonObject { [UserToken] = utoken, [CardToken] = ctoken }.ToJsonString(),
            Last4 = stored.TextAt("last_4"),
            ExpMonth = int.TryParse(stored.TextAt("month"), CultureInfo.InvariantCulture, out var month) ? month : null,
            ExpYear = int.TryParse(stored.TextAt("year"), CultureInfo.InvariantCulture, out var year) ? year < 100 ? 2000 + year : year : null,
            Issuer = stored.TextAt("c_bank"),
            Brand = stored.TextAt("schema")?.ToLowerInvariant(),
            Funding = stored.TextAt("c_type")?.ToLowerInvariant(),
        };
    }

    private static (string UserToken, string CardToken)? Card(string token)
    {
        try
        {
            return JsonNode.Parse(token) is { } node && node[UserToken]?.GetValue<string>() is { } utoken && node[CardToken]?.GetValue<string>() is { } ctoken ? (utoken, ctoken) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

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
