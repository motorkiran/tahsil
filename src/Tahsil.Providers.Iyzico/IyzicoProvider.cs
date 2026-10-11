using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Iyzico;

/// <summary>
/// iyzico (docs/specs/providers/iyzico.md): customers pay on iyzico's hosted Checkout Form with 3D Secure, the card is
/// stored with iyzico's card storage, renewals charge it through <c>/payment/auth</c>, and V3-signed notifications are
/// triggers for reconciliation.
/// </summary>
public sealed class IyzicoProvider : IPaymentProvider
{
    public const string Key = "iyzico";

    /// <summary>How long a Checkout Form token can be paid (IYZ-6).</summary>
    public static readonly TimeSpan CheckoutFormLifetime = TimeSpan.FromMinutes(30);

    private const string TokenData = "token";
    private const string TokenExpiresData = "tokenExpiresAt";
    private const string TransactionData = "paymentTransactionId";

    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private static readonly string[] Currencies = ["TRY", "USD", "EUR", "GBP", "NOK", "CHF"];

    /// <summary>Every currency iyzico takes has two minor-unit digits.</summary>
    private const int Exponent = 2;

    public ProviderDescriptor Descriptor { get; } = new()
    {
        Key = Key,
        DisplayName = "iyzico",
        Credentials =
        [
            new FieldDefinition { Key = "apiKey", Label = "API key", Type = FieldType.Secret, Description = "Settings → Merchant Settings → API Keys; sandbox keys start with sandbox-." },
            new FieldDefinition { Key = "secretKey", Label = "Secret key", Type = FieldType.Secret },
        ],
        Settings =
        [
            new FieldDefinition
            {
                Key = "identityNumberFallback",
                Label = "Identity number for buyers without one",
                Required = false,
                Default = IyzicoBuyer.DefaultIdentityNumber,
                Pattern = "^[0-9A-Za-z]{5,50}$",
                Description = "iyzico requires an identity number; sent when the customer has no tax ID.",
            },
            new FieldDefinition { Key = "fallbackAddress", Label = "Address for buyers without one", Required = false, Default = IyzicoBuyer.DefaultAddress },
            new FieldDefinition { Key = "fallbackCity", Label = "City for buyers without one", Required = false, Default = IyzicoBuyer.DefaultCity },
            new FieldDefinition { Key = "fallbackCountry", Label = "Country for buyers without one", Required = false, Default = IyzicoBuyer.DefaultCountry },
        ],
        Currencies = new HashSet<string>(Currencies, StringComparer.Ordinal),
        MinimumAmounts = Currencies.ToDictionary(currency => currency, _ => 100L, StringComparer.Ordinal),
        Capabilities = new ProviderCapabilities
        {
            Entry = new HashSet<CardEntry> { CardEntry.Redirect },
            SavesCards = CardSaving.DuringPaymentWhenCustomerAgrees,
            Setup = CardSetup.VerificationCharge,
            ChargesSavedCards = true,
            ChargingSavedCardsRequiresMerchantPermission = true,
            Refunds = RefundSupport.PartialAfterSettlementDay,
            Voids = VoidSupport.SameDayFullCancel,
            FindByReference = ReferenceSearch.Any,
            Notifications = NotificationSupport.HintsOnly,
            IdempotentCharges = false,
        },
        CustomerData = new CustomerDataRequirements(
            [
                new(CustomerField.Name, null),
                new(CustomerField.Email, null),
                new(CustomerField.IdentityNumber, IyzicoBuyer.DefaultIdentityNumber),
                new(CustomerField.Address, IyzicoBuyer.DefaultAddress),
                new(CustomerField.City, IyzicoBuyer.DefaultCity),
                new(CustomerField.Country, IyzicoBuyer.DefaultCountry),
                new(CustomerField.IpAddress, "127.0.0.1"),
            ],
            [
                new(CustomerField.IdentityNumber, IyzicoBuyer.DefaultIdentityNumber),
                new(CustomerField.Address, IyzicoBuyer.DefaultAddress),
                new(CustomerField.IpAddress, "127.0.0.1"),
            ]),
        ContentSecurity = new Dictionary<ProviderEnvironment, ContentSecuritySources>
        {
            [ProviderEnvironment.Sandbox] = ContentSecuritySources.None,
            [ProviderEnvironment.Production] = ContentSecuritySources.None,
        },
    };

    /// <summary>Reachability, then a signed BIN check that proves the keys (§3.2).</summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        using var answer = await IyzicoApi.PostAsync(context, "/payment/bin/check", new JsonObject
        {
            ["locale"] = "tr",
            ["binNumber"] = "552608",
            ["conversationId"] = $"tahsil-test-{Guid.CreateVersion7():N}",
        }, ProviderHttp.LookupTimeout, cancellationToken);

        if (answer.Unanswered)
        {
            return new ConnectionTestResult(false, "unreachable", "iyzico did not answer; try again later.");
        }

        return answer.Succeeded
            ? ConnectionTestResult.Success
            : int.TryParse(answer.ErrorCode, out var code) && code is >= 1000 and <= 1009
                ? new ConnectionTestResult(false, "invalid_credentials", "iyzico rejected the keys (check sandbox and production keys).")
                : new ConnectionTestResult(false, answer.ErrorCode, answer.ErrorMessage);
    }

    /// <summary>Initializes a Checkout Form and sends the customer to iyzico's page (IYZ-6).</summary>
    public async Task<StartResult> StartAsync(ProviderContext context, StartRequest request, CancellationToken cancellationToken)
    {
        var amount = AmountFormat.Fixed(request.Amount);
        var body = Common(request.Locale, request.Reference, request.Amount, request.PaymentId.ToString());
        body["paymentGroup"] = request.SaveCard ? "SUBSCRIPTION" : "PRODUCT";
        body["callbackUrl"] = request.ReturnUrl.AbsoluteUri;
        body["enabledInstallments"] = new JsonArray(1);
        if (request.ProviderCustomer is { } cardUserKey)
        {
            body["cardUserKey"] = cardUserKey;
        }

        body["buyer"] = IyzicoBuyer.Buyer(context, request.Customer);
        body["billingAddress"] = IyzicoBuyer.BillingAddress(context, request.Customer);
        body["basketItems"] = Basket(request.PaymentId, request.Description ?? (request.Lines.Count > 0 ? request.Lines[0].Description : null), request.SaveCard, amount);

        using var answer = await IyzicoApi.PostAsync(context, "/payment/iyzipos/checkoutform/initialize/auth/ecom", body, ProviderHttp.LookupTimeout, cancellationToken);
        if (answer.Unanswered)
        {
            // Initializing charges nothing; the customer may simply try again.
            return StartResult.Failed(PaymentOutcome.Failure(FailureCode.ProviderUnavailable, providerMessage: "iyzico did not answer the Checkout Form initialization."));
        }

        if (!answer.Succeeded)
        {
            return StartResult.Failed(PaymentOutcome.Failure(IyzicoFailures.Map(answer.ErrorGroup, answer.ErrorCode, customerPresent: true), answer.ErrorCode, answer.ErrorMessage));
        }

        var token = answer.Root.TextAt("token");
        if (token is null || answer.Root.TextAt("paymentPageUrl") is not { } page
            || !IyzicoSigning.ResponseMatches(context.Credential("secretKey"), answer.Root.TextAt("signature"), answer.Root.TextAt("conversationId"), token))
        {
            return StartResult.Failed(PaymentOutcome.Failure(FailureCode.ProviderError, providerMessage: "iyzico's Checkout Form answer was not signed as expected."));
        }

        var expiresAt = context.Time.GetUtcNow() + CheckoutFormLifetime;
        return new StartResult
        {
            Presentation = new Presentation.Redirect(new Uri(page)),
            ProviderData = new Dictionary<string, string>
            {
                [TokenData] = token,
                [TokenExpiresData] = expiresAt.ToString("O", CultureInfo.InvariantCulture),
            },
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>Retrieves the Checkout Form's result (IYZ-7); the posted token only has to be the payment's own.</summary>
    public async Task<PaymentOutcome> CompleteAsync(ProviderContext context, CompleteRequest request, CancellationToken cancellationToken)
    {
        if (request.ProviderData.GetValueOrDefault(TokenData) is not { } token)
        {
            return PaymentOutcome.Unknown("The payment has no Checkout Form token.");
        }

        if (request.ReturnValues.GetValueOrDefault("token") is { } posted && posted != token)
        {
            return PaymentOutcome.Unknown("The return named another Checkout Form.");
        }

        using var answer = await IyzicoApi.PostAsync(context, "/payment/iyzipos/checkoutform/auth/ecom/detail", new JsonObject
        {
            ["locale"] = "tr",
            ["conversationId"] = request.Reference,
            ["token"] = token,
        }, ProviderHttp.LookupTimeout, cancellationToken);

        if (answer.Unanswered)
        {
            return PaymentOutcome.Unknown("iyzico did not answer the Checkout Form retrieval.");
        }

        if (!answer.Succeeded)
        {
            return answer.ErrorCode is "5124" or "5326" or "5125"
                ? PaymentOutcome.Failure(FailureCode.Abandoned, answer.ErrorCode, answer.ErrorMessage)
                : PaymentOutcome.Failure(IyzicoFailures.Map(answer.ErrorGroup, answer.ErrorCode, customerPresent: true), answer.ErrorCode, answer.ErrorMessage);
        }

        var root = answer.Root;
        var paymentStatus = root.TextAt("paymentStatus");
        if (paymentStatus is "INIT_THREEDS" or "CALLBACK_THREEDS")
        {
            var expired = DateTimeOffset.TryParse(request.ProviderData.GetValueOrDefault(TokenExpiresData), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt)
                && context.Time.GetUtcNow() >= expiresAt;
            return expired ? PaymentOutcome.Failure(FailureCode.AuthenticationFailed, paymentStatus) : new PaymentOutcome { Status = PaymentStatus.Pending };
        }

        if (paymentStatus != "SUCCESS")
        {
            return PaymentOutcome.Failure(IyzicoFailures.Map(root.TextAt("errorGroup"), root.TextAt("errorCode"), customerPresent: true), root.TextAt("errorCode"), root.TextAt("errorMessage"));
        }

        if (!IyzicoSigning.ResponseMatches(
            context.Credential("secretKey"),
            root.TextAt("signature"),
            paymentStatus,
            root.TextAt("paymentId"),
            root.TextAt("currency"),
            root.TextAt("basketId"),
            root.TextAt("conversationId"),
            Price(root, "paidPrice"),
            Price(root, "price"),
            root.TextAt("token")))
        {
            return PaymentOutcome.Unknown("iyzico's Checkout Form result was not signed as expected.");
        }

        var outcome = Paid(root, request.Amount, ThreeDSecureResult.Authenticated);
        if (outcome.Status == PaymentStatus.Succeeded && root.TextAt("cardUserKey") is { } cardUserKey && root.TextAt("cardToken") is { } cardToken)
        {
            outcome = outcome with { SavedCard = await SavedCardAsync(context, root, cardUserKey, cardToken, cancellationToken) };
        }

        return outcome;
    }

    /// <summary>Charges a stored card without 3D Secure (IYZ-9).</summary>
    public async Task<PaymentOutcome> ChargeSavedCardAsync(ProviderContext context, ChargeRequest request, CancellationToken cancellationToken)
    {
        var body = Common(request.Customer.Locale, request.Reference, request.Amount, request.PaymentId.ToString());
        body["installment"] = 1;
        body["paymentChannel"] = "WEB";
        body["paymentGroup"] = "SUBSCRIPTION";
        body["paymentCard"] = new JsonObject { ["cardUserKey"] = request.SavedCard.ProviderCustomer, ["cardToken"] = request.SavedCard.Token };
        body["buyer"] = IyzicoBuyer.Buyer(context, request.Customer);
        body["billingAddress"] = IyzicoBuyer.BillingAddress(context, request.Customer);
        body["basketItems"] = Basket(request.PaymentId, request.Description ?? (request.Lines.Count > 0 ? request.Lines[0].Description : null), subscription: true, AmountFormat.Fixed(request.Amount));

        using var answer = await IyzicoApi.PostAsync(context, "/payment/auth", body, ProviderHttp.ChangeTimeout, cancellationToken);
        if (answer.Unanswered)
        {
            // The charge may have been made: reconciliation finds out (ADP-4).
            return PaymentOutcome.Unknown("iyzico did not answer the charge.");
        }

        if (!answer.Succeeded)
        {
            var failure = answer.ErrorCode == "3007" ? FailureCode.MerchantNotPermitted : IyzicoFailures.Map(answer.ErrorGroup, answer.ErrorCode, customerPresent: false);
            return failure == FailureCode.ProviderUnavailable
                ? PaymentOutcome.Unknown(answer.ErrorMessage)
                : PaymentOutcome.Failure(failure, answer.ErrorCode, answer.ErrorMessage);
        }

        var root = answer.Root;
        if (!IyzicoSigning.ResponseMatches(
            context.Credential("secretKey"),
            root.TextAt("signature"),
            root.TextAt("paymentId"),
            root.TextAt("currency"),
            root.TextAt("basketId"),
            root.TextAt("conversationId"),
            Price(root, "paidPrice"),
            Price(root, "price")))
        {
            return PaymentOutcome.Unknown("iyzico's charge answer was not signed as expected.");
        }

        return Paid(root, request.Amount, ThreeDSecureResult.NotAttempted);
    }

    /// <summary>Looks a payment up by Tahsil's reference through reporting (IYZ-10).</summary>
    public async Task<PaymentLookup> FindPaymentAsync(ProviderContext context, PaymentQuery query, CancellationToken cancellationToken)
    {
        using var answer = await IyzicoApi.GetAsync(
            context,
            "/v2/reporting/payment/details",
            $"paymentConversationId={Uri.EscapeDataString(query.Reference)}&locale=tr&conversationId=tahsil-{Guid.CreateVersion7():N}",
            ProviderHttp.LookupTimeout,
            cancellationToken);
        if (answer.Unanswered || !answer.Succeeded)
        {
            return new PaymentLookup.Unavailable(answer.ErrorMessage ?? "iyzico did not answer the payment lookup.");
        }

        var payments = answer.Root.ArrayAt("payments").ToList();
        if (payments.Count == 0)
        {
            return new PaymentLookup.NotFound();
        }

        if (payments.FirstOrDefault(payment => payment.Int64At("paymentStatus") == 1) is { ValueKind: JsonValueKind.Object } success)
        {
            var outcome = success.Int64At("fraudStatus") switch
            {
                0 => new PaymentOutcome { Status = PaymentStatus.Pending },
                -1 => PaymentOutcome.Failure(FailureCode.FraudSuspected, "fraudStatus=-1"),
                _ => new PaymentOutcome
                {
                    Status = PaymentStatus.Succeeded,
                    ProviderPaymentId = success.TextAt("paymentId"),
                    ChargedAmount = Charged(success, success.TextAt("currency"), Exponent),
                    ProviderData = Transaction(success),
                },
            };
            return new PaymentLookup.Found(outcome);
        }

        if (payments.Any(payment => payment.Int64At("paymentStatus") == 3))
        {
            return new PaymentLookup.Found(new PaymentOutcome { Status = PaymentStatus.Pending });
        }

        var failed = payments.First();
        return new PaymentLookup.Found(PaymentOutcome.Failure(
            IyzicoFailures.Map(failed.TextAt("errorGroup"), failed.TextAt("errorCode"), customerPresent: true), failed.TextAt("errorCode"), failed.TextAt("errorMessage")));
    }

    /// <summary>A same-day full refund cancels the payment; otherwise its transaction is refunded (IYZ-11).</summary>
    public async Task<RefundOutcome> RefundAsync(ProviderContext context, RefundRequest request, CancellationToken cancellationToken)
    {
        var today = TimeZoneInfo.ConvertTime(context.Time.GetUtcNow(), Istanbul).Date;
        var paidOn = TimeZoneInfo.ConvertTime(request.PaymentSucceededAt, Istanbul).Date;
        var cancel = today == paidOn && request.Amount.MinorUnits == request.PaymentAmount.MinorUnits;
        IyzicoAnswer answer;
        if (cancel)
        {
            answer = await IyzicoApi.PostAsync(context, "/payment/cancel", new JsonObject
            {
                ["locale"] = "tr",
                ["conversationId"] = request.Reference,
                ["paymentId"] = request.ProviderPaymentId,
            }, ProviderHttp.ChangeTimeout, cancellationToken);
        }
        else if (request.PaymentProviderData.GetValueOrDefault(TransactionData) is { } transaction)
        {
            answer = await IyzicoApi.PostAsync(context, "/payment/refund", new JsonObject
            {
                ["locale"] = "tr",
                ["conversationId"] = request.Reference,
                ["paymentTransactionId"] = transaction,
                ["price"] = AmountFormat.Fixed(request.Amount),
                ["currency"] = request.Amount.Currency,
            }, ProviderHttp.ChangeTimeout, cancellationToken);
        }
        else
        {
            return new RefundOutcome { Status = RefundStatus.Failed, FailureCode = "payment_not_refundable", FailureMessage = "The payment has no iyzico transaction ID." };
        }

        using (answer)
        {
            if (answer.Unanswered)
            {
                return RefundOutcome.Unknown("iyzico did not answer the refund.");
            }

            if (!answer.Succeeded)
            {
                return new RefundOutcome
                {
                    Status = RefundStatus.Failed,
                    Kind = cancel ? RefundKind.Void : RefundKind.Refund,
                    FailureCode = answer.ErrorCode == "10223" ? "refund.not_allowed_yet" : answer.ErrorCode is { } code ? $"iyzico_{code}" : "refund_declined",
                    FailureMessage = answer.ErrorMessage,
                };
            }

            return new RefundOutcome
            {
                Status = RefundStatus.Succeeded,
                Kind = cancel ? RefundKind.Void : RefundKind.Refund,
                ProviderRefundId = answer.Root.TextAt(cancel ? "cancelHostReference" : "refundHostReference") ?? answer.Root.TextAt("paymentId"),
            };
        }
    }

    /// <summary>Finds a refund among the payment's cancels and refunds, by its reference (§3.8).</summary>
    public async Task<RefundLookup> FindRefundAsync(ProviderContext context, RefundQuery query, CancellationToken cancellationToken)
    {
        if (query.ProviderPaymentId is not { } paymentId)
        {
            return new RefundLookup.NotFound();
        }

        using var answer = await IyzicoApi.GetAsync(
            context, "/v2/reporting/payment/details", $"paymentId={Uri.EscapeDataString(paymentId)}&locale=tr", ProviderHttp.LookupTimeout, cancellationToken);
        if (answer.Unanswered || !answer.Succeeded)
        {
            return new RefundLookup.Unavailable(answer.ErrorMessage ?? "iyzico did not answer the refund lookup.");
        }

        foreach (var payment in answer.Root.ArrayAt("payments"))
        {
            if (payment.ArrayAt("cancels").Any(cancel => cancel.TextAt("conversationId") == query.Reference))
            {
                return new RefundLookup.Found(new RefundOutcome { Status = RefundStatus.Succeeded, Kind = RefundKind.Void });
            }

            if (payment.ArrayAt("itemTransactions").SelectMany(item => item.ArrayAt("refunds")).Any(refund => refund.TextAt("conversationId") == query.Reference))
            {
                return new RefundLookup.Found(new RefundOutcome { Status = RefundStatus.Succeeded });
            }
        }

        return new RefundLookup.NotFound();
    }

    /// <summary>Deletes a stored card (§3.9); a card iyzico no longer has counts as removed.</summary>
    public async Task<RemoveCardResult> RemoveSavedCardAsync(ProviderContext context, SavedCardReference card, CancellationToken cancellationToken)
    {
        using var answer = await IyzicoApi.DeleteAsync(context, "/cardstorage/card", new JsonObject
        {
            ["locale"] = "tr",
            ["conversationId"] = $"tahsil-{Guid.CreateVersion7():N}",
            ["cardUserKey"] = card.ProviderCustomer,
            ["cardToken"] = card.Token,
        }, ProviderHttp.LookupTimeout, cancellationToken);

        return answer.Succeeded || answer.ErrorCode is "3005" or "3006"
            ? new RemoveCardResult(true)
            : new RemoveCardResult(false, answer.Unanswered ? "iyzico did not answer." : answer.ErrorMessage);
    }

    /// <summary>
    /// Verifies the V3 signature (IYZ-12). A verified notification is still only a trigger, since the signature covers no
    /// timestamp: the engine asks iyzico how the payment stands (IYZ-13).
    /// </summary>
    public Task<NotificationResult> HandleNotificationAsync(ProviderContext context, IncomingNotification notification, CancellationToken cancellationToken)
    {
        using var document = LenientJson.TryParse(Encoding.UTF8.GetString(notification.Body));
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root || root.TextAt("paymentConversationId") is not { Length: > 0 } reference)
        {
            return Task.FromResult(NotificationResult.Rejected(new NotificationResponse(400, "text/plain", "Unreadable notification")));
        }

        var eventType = root.TextAt("iyziEventType");
        var expected = eventType == "CHECKOUT_FORM_AUTH"
            ? IyzicoSigning.Notification(context.Credential("secretKey"), eventType, root.TextAt("iyziPaymentId"), root.TextAt("token"), reference, root.TextAt("status"))
            : IyzicoSigning.Notification(context.Credential("secretKey"), eventType, root.TextAt("paymentId"), reference, root.TextAt("status"));
        var signature = notification.Headers.FirstOrDefault(header => header.Key.Equals("X-IYZ-SIGNATURE-V3", StringComparison.OrdinalIgnoreCase)).Value;
        if (!Hmac.MatchesHex(expected, signature))
        {
            return Task.FromResult(NotificationResult.Rejected(new NotificationResponse(400, "text/plain", "Invalid signature")));
        }

        return Task.FromResult(new NotificationResult
        {
            Verified = true,
            ProviderEventId = root.TextAt("iyziReferenceCode"),
            EventType = eventType,
            Response = NotificationResponse.Ok,
            Updates = [new NotificationUpdate.CheckPayment(reference)],
        });
    }

    /// <summary>The fields every payment request carries (IYZ-5).</summary>
    private static JsonObject Common(string locale, string reference, Amount amount, string basketId)
    {
        var price = AmountFormat.Fixed(amount);
        return new JsonObject
        {
            ["locale"] = locale == "tr" ? "tr" : "en",
            ["conversationId"] = reference,
            ["price"] = price,
            ["paidPrice"] = price,
            ["currency"] = amount.Currency,
            ["basketId"] = basketId,
        };
    }

    /// <summary>One virtual item for the whole amount, so the item sum is the price and refunds address one transaction.</summary>
    private static JsonArray Basket(Guid id, string? description, bool subscription, string amount) =>
    [
        new JsonObject
        {
            ["id"] = id.ToString(),
            ["name"] = string.IsNullOrWhiteSpace(description) ? "Payment" : description.Length > 100 ? description[..100] : description,
            ["category1"] = subscription ? "Subscription" : "Digital product",
            ["itemType"] = "VIRTUAL",
            ["price"] = amount,
        },
    ];

    /// <summary>A successful payment's outcome by its fraud status (§3.4): approved, under review, or refused.</summary>
    private static PaymentOutcome Paid(JsonElement root, Amount expected, ThreeDSecureResult threeDSecure) => root.Int64At("fraudStatus") switch
    {
        0 => new PaymentOutcome { Status = PaymentStatus.Pending, ProviderPaymentId = root.TextAt("paymentId") },
        -1 => PaymentOutcome.Failure(FailureCode.FraudSuspected, "fraudStatus=-1"),
        _ => new PaymentOutcome
        {
            Status = PaymentStatus.Succeeded,
            ProviderPaymentId = root.TextAt("paymentId"),
            ChargedAmount = Charged(root, root.TextAt("currency") ?? expected.Currency, expected.Exponent),
            ThreeDSecure = threeDSecure,
            ProviderData = Transaction(root),
        },
    };

    /// <summary>What iyzico says it charged, in minor units when exact (IYZ-14).</summary>
    private static Amount? Charged(JsonElement root, string? currency, int exponent) =>
        currency is not null && root.DecimalAt("paidPrice") is { } paid && AmountFormat.ToMinorUnits(paid, exponent) is { } minor ? new Amount(minor, currency, exponent) : null;

    private static Dictionary<string, string> Transaction(JsonElement root) =>
        root.ArrayAt("itemTransactions").Select(item => item.TextAt("paymentTransactionId")).FirstOrDefault(id => id is not null) is { } transaction
            ? new Dictionary<string, string> { [TransactionData] = transaction }
            : [];

    /// <summary>A price as the response signature covers it (IYZ-4, IYZ-14).</summary>
    private static string? Price(JsonElement root, string name) => root.DecimalAt(name) is { } price ? IyzicoSigning.Price(price) : null;

    /// <summary>The saved card of a Checkout Form (IYZ-8), with its bank and expiry from the card list when iyzico gives them.</summary>
    private static async Task<SavedCard> SavedCardAsync(ProviderContext context, JsonElement root, string cardUserKey, string cardToken, CancellationToken cancellationToken)
    {
        var card = new SavedCard
        {
            ProviderCustomer = cardUserKey,
            Token = cardToken,
            Bin = root.TextAt("binNumber"),
            Last4 = root.TextAt("lastFourDigits"),
            Brand = root.TextAt("cardAssociation") switch
            {
                "VISA" => "visa",
                "MASTER_CARD" => "mastercard",
                "AMERICAN_EXPRESS" => "amex",
                "TROY" => "troy",
                var other => other?.ToLowerInvariant(),
            },
            Funding = root.TextAt("cardType") switch
            {
                "CREDIT_CARD" => "credit",
                "DEBIT_CARD" => "debit",
                "PREPAID_CARD" => "prepaid",
                _ => null,
            },
            CardFamily = root.TextAt("cardFamily"),
        };

        using var list = await IyzicoApi.PostAsync(context, "/cardstorage/cards", new JsonObject
        {
            ["locale"] = "tr",
            ["conversationId"] = $"tahsil-{Guid.CreateVersion7():N}",
            ["cardUserKey"] = cardUserKey,
        }, ProviderHttp.LookupTimeout, cancellationToken);
        if (!list.Succeeded || list.Root.ArrayAt("cardDetails").FirstOrDefault(detail => detail.TextAt("cardToken") == cardToken) is not { ValueKind: JsonValueKind.Object } details)
        {
            return card;
        }

        return card with
        {
            Issuer = details.TextAt("cardBankName"),
            ExpMonth = int.TryParse(details.TextAt("expireMonth"), CultureInfo.InvariantCulture, out var month) ? month : null,
            ExpYear = int.TryParse(details.TextAt("expireYear"), CultureInfo.InvariantCulture, out var year) ? year : null,
        };
    }
}
