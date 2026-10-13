using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Craftgate;

/// <summary>
/// Craftgate (docs/specs/providers/craftgate.md): customers pay on the Common Payment Page, Craftgate's vault keeps
/// the card, renewals are charged without 3D Secure on the merchant's chosen POS (such as Kuveyt Türk), and signed
/// webhooks are triggers for reconciliation.
/// </summary>
public sealed class CraftgateProvider : IPaymentProvider
{
    public const string Key = "craftgate";

    private const string TokenData = "token";
    private const string TransactionData = "paymentTransactionId";
    private const string PosAlias = "posAlias";

    private static readonly TimeSpan IdempotentRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly string[] Currencies = ["TRY", "USD", "EUR", "GBP"];
    private static readonly string[] Pending = ["INIT_THREEDS", "CALLBACK_THREEDS", "WAITING"];

    public ProviderDescriptor Descriptor { get; } = new()
    {
        Key = Key,
        DisplayName = "Craftgate",
        Credentials =
        [
            new FieldDefinition { Key = "apiKey", Label = "API key", Type = FieldType.Secret, Description = "Management → API Authorization Management in Craftgate's panel." },
            new FieldDefinition { Key = "secretKey", Label = "Secret key", Type = FieldType.Secret },
            new FieldDefinition { Key = "webhookKey", Label = "Merchant webhook key", Type = FieldType.Secret, Description = "Settings → Merchant Settings → General Settings." },
        ],
        Settings =
        [
            new FieldDefinition
            {
                Key = PosAlias,
                Label = "POS for renewals",
                Required = false,
                Description = "The alias of the POS that charges saved cards, such as your Kuveyt Türk POS; it must allow payments without 3D Secure and without CVC. Empty lets Craftgate's routing choose.",
            },
        ],
        Currencies = new HashSet<string>(Currencies, StringComparer.Ordinal),
        MinimumAmounts = Currencies.ToDictionary(currency => currency, _ => 100L, StringComparer.Ordinal),
        Capabilities = new ProviderCapabilities
        {
            Entry = new HashSet<CardEntry> { CardEntry.Redirect },
            SavesCards = CardSaving.DuringPayment,
            Setup = CardSetup.VerificationCharge,
            ChargesSavedCards = true,
            ChargingSavedCardsRequiresMerchantPermission = true,
            Refunds = RefundSupport.PartialAfterSettlementDay,
            Voids = VoidSupport.SameDayFullCancel,
            FindByReference = ReferenceSearch.Any,
            Notifications = NotificationSupport.HintsOnly,
            IdempotentCharges = true,
        },
        CustomerData = CustomerDataRequirements.None,
        ContentSecurity = new Dictionary<ProviderEnvironment, ContentSecuritySources>
        {
            [ProviderEnvironment.Sandbox] = ContentSecuritySources.None,
            [ProviderEnvironment.Production] = ContentSecuritySources.None,
        },
    };

    /// <summary>A payment search proves the keys; with a renewal POS, the POS is checked for what renewals need (§3.2).</summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        using var search = await CraftgateApi.SendAsync(
            context, HttpMethod.Get, $"payment-reporting/v1/payments?{CraftgateApi.Query(("conversationId", $"tahsil-test-{Guid.CreateVersion7():N}"), ("page", "0"), ("size", "1"))}",
            null, CraftgateApi.LookupTimeout, cancellationToken);
        if (search.Unanswered)
        {
            return new ConnectionTestResult(false, "unreachable", "Craftgate did not answer; try again later.");
        }

        if (!search.Succeeded)
        {
            return new ConnectionTestResult(false, "invalid_credentials", $"Craftgate refused the keys: {search.Message}");
        }

        if (context.Setting(PosAlias) is not { Length: > 0 } alias)
        {
            return ConnectionTestResult.Success;
        }

        using var poses = await CraftgateApi.SendAsync(
            context, HttpMethod.Get, $"merchant/v1/merchant-poses?{CraftgateApi.Query(("alias", alias))}", null, CraftgateApi.LookupTimeout, cancellationToken);
        if (!poses.Succeeded)
        {
            return new ConnectionTestResult(false, poses.Unanswered ? "unreachable" : "pos_not_found", poses.Message);
        }

        if (poses.Data.ArrayAt("items").FirstOrDefault(pos => pos.TextAt("alias") == alias) is not { ValueKind: JsonValueKind.Object } found)
        {
            return new ConnectionTestResult(false, "pos_not_found", $"Craftgate has no POS with the alias {alias}.");
        }

        var missing = new List<string>();
        if (found.TextAt("status") != "ACTIVE")
        {
            missing.Add("it is not active");
        }

        if (found.BooleanAt("enablePaymentWithoutCvc") != true)
        {
            missing.Add("payments without CVC are not enabled");
        }

        if (found.BooleanAt("forceThreeDs") == true)
        {
            missing.Add("it forces 3D Secure");
        }

        return missing.Count == 0
            ? ConnectionTestResult.Success
            : new ConnectionTestResult(false, "pos_not_ready", $"The POS {alias} cannot charge renewals: {string.Join(", ", missing)}.");
    }

    /// <summary>Initializes the Common Payment Page (CRG-6), with 3D Secure and the card stored when asked.</summary>
    public async Task<StartResult> StartAsync(ProviderContext context, StartRequest request, CancellationToken cancellationToken)
    {
        var price = CraftgateApi.Price(request.Amount);
        var body = new JsonObject
        {
            ["price"] = price.DeepClone(),
            ["paidPrice"] = price.DeepClone(),
            ["currency"] = request.Amount.Currency,
            ["paymentGroup"] = request.SaveCard ? "LISTING_OR_SUBSCRIPTION" : "PRODUCT",
            ["conversationId"] = request.Reference,
            ["externalId"] = request.PaymentId.ToString(),
            ["callbackUrl"] = request.ReturnUrl.AbsoluteUri,
            ["enabledPaymentMethods"] = new JsonArray("CARD"),
            ["enabledInstallments"] = new JsonArray(1),
            ["forceThreeDS"] = true,
            ["alwaysStoreCardAfterPayment"] = request.SaveCard,
            ["allowDeleteStoredCard"] = false,
            ["ttl"] = 30,
            ["items"] = new JsonArray(Item(request.Description ?? (request.Lines.Count > 0 ? request.Lines[0].Description : "Payment"), price, request.PaymentId.ToString())),
            ["fraudParams"] = new JsonObject { ["buyerExternalId"] = request.Customer.Id.ToString(), ["buyerEmail"] = request.Customer.Email },
        };
        if (request.ProviderCustomer is { } cardUserKey)
        {
            body["cardUserKey"] = cardUserKey;
        }

        using var answer = await CraftgateApi.SendAsync(
            context, HttpMethod.Post, "payment/v1/checkout-payments/init", body, CraftgateApi.LookupTimeout, cancellationToken, request.Reference);
        if (!answer.Succeeded || answer.Data.TextAt("pageUrl") is not { } pageUrl || answer.Data.TextAt("token") is not { } token)
        {
            // Initializing charges nothing; the customer may simply try again.
            return StartResult.Failed(PaymentOutcome.Failure(
                answer.Unanswered ? FailureCode.ProviderUnavailable : CraftgateFailures.Map(answer.ErrorGroup, answer.ErrorCode), answer.ErrorCode, answer.Message));
        }

        var lang = request.Locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en";
        return new StartResult
        {
            Presentation = new Presentation.Redirect(new Uri($"{pageUrl}{(pageUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?')}lang={lang}")),
            ProviderData = new Dictionary<string, string> { [TokenData] = token },
            ExpiresAt = answer.Data.DateTimeAt("tokenExpireDate") ?? context.Time.GetUtcNow().AddMinutes(30),
        };
    }

    /// <summary>
    /// The page posts its token back unsigned, so it must be this payment's; the result is read from Craftgate (CRG-7),
    /// with the stored card (CRG-8).
    /// </summary>
    public async Task<PaymentOutcome> CompleteAsync(ProviderContext context, CompleteRequest request, CancellationToken cancellationToken)
    {
        if (!request.ProviderData.TryGetValue(TokenData, out var token)
            || (request.ReturnValues.TryGetValue("token", out var posted) && posted != token))
        {
            return PaymentOutcome.Failure(FailureCode.InvalidRequest, providerMessage: "The returned token is not this payment's.");
        }

        using var answer = await CraftgateApi.SendAsync(context, HttpMethod.Get, $"payment/v1/checkout-payments/{Uri.EscapeDataString(token)}", null, CraftgateApi.LookupTimeout, cancellationToken);
        if (answer.Unanswered)
        {
            return PaymentOutcome.Unknown(answer.Message);
        }

        if (!answer.Succeeded)
        {
            // The token expired, or the page was never paid.
            return PaymentOutcome.Failure(FailureCode.Abandoned, answer.ErrorCode, answer.Message);
        }

        var outcome = Outcome(answer.Data);
        if (outcome.Status != PaymentStatus.Succeeded || !request.SaveCard)
        {
            return outcome;
        }

        return outcome with { SavedCard = await SavedCardAsync(context, answer.Data, request.ProviderCustomer, cancellationToken) };
    }

    /// <summary>A card payment with the stored card on the renewal POS, without retrying elsewhere (CRG-9), resent once with its key when unanswered (CRG-10).</summary>
    public async Task<PaymentOutcome> ChargeSavedCardAsync(ProviderContext context, ChargeRequest request, CancellationToken cancellationToken)
    {
        if (Card(request.SavedCard.Token) is not { } card)
        {
            return PaymentOutcome.Failure(FailureCode.PaymentMethodMissing, providerMessage: "The saved card's token is not a Craftgate card.");
        }

        var price = CraftgateApi.Price(request.Amount);
        var body = new JsonObject
        {
            ["price"] = price.DeepClone(),
            ["paidPrice"] = price.DeepClone(),
            ["walletPrice"] = 0,
            ["installment"] = 1,
            ["currency"] = request.Amount.Currency,
            ["paymentGroup"] = "LISTING_OR_SUBSCRIPTION",
            ["paymentPhase"] = "AUTH",
            ["conversationId"] = request.Reference,
            ["externalId"] = request.PaymentId.ToString(),
            ["retry"] = false,
            ["card"] = new JsonObject { ["cardUserKey"] = card.UserKey, ["cardToken"] = card.Token },
            ["items"] = new JsonArray(Item(request.Description ?? "Payment", price, request.PaymentId.ToString())),
        };
        if (context.Setting(PosAlias) is { Length: > 0 } alias)
        {
            body["posAlias"] = alias;
        }

        if (request.Customer.IpAddress is { } ip)
        {
            body["clientIp"] = ip;
        }

        for (var attempt = 1; ; attempt++)
        {
            using var answer = await CraftgateApi.SendAsync(
                context, HttpMethod.Post, "payment/v1/card-payments", body, CraftgateApi.PaymentTimeout, cancellationToken, request.Reference);
            if (answer.Unanswered)
            {
                if (attempt == 1)
                {
                    // Craftgate answers a repeated key with the first request's result.
                    await Task.Delay(IdempotentRetryDelay, context.Time, cancellationToken);
                    continue;
                }

                return PaymentOutcome.Unknown(answer.Message);
            }

            if (answer.Succeeded)
            {
                return Outcome(answer.Data);
            }

            // An earlier sale with this reference succeeded: reconciliation finds it.
            if (answer.ErrorGroup is "ORDER_ID_ALREADY_USED" or "APPROVED_COMPLETED" || answer.ErrorDescription?.Contains("ORDER_ID_ALREADY_USED", StringComparison.Ordinal) == true)
            {
                return PaymentOutcome.Unknown(answer.Message);
            }

            return PaymentOutcome.Failure(CraftgateFailures.Map(answer.ErrorGroup, answer.ErrorCode), answer.ErrorGroup ?? answer.ErrorCode, answer.ErrorDescription);
        }
    }

    /// <summary>The payments with Tahsil's reference since a day before the payment (CRG-11).</summary>
    public async Task<PaymentLookup> FindPaymentAsync(ProviderContext context, PaymentQuery query, CancellationToken cancellationToken)
    {
        using var answer = await CraftgateApi.SendAsync(
            context,
            HttpMethod.Get,
            $"payment-reporting/v1/payments?{CraftgateApi.Query(("conversationId", query.Reference), ("minCreatedDate", CraftgateApi.LocalDate(query.CreatedAt.AddDays(-1))), ("page", "0"), ("size", "10"))}",
            null,
            CraftgateApi.LookupTimeout,
            cancellationToken);
        if (!answer.Succeeded)
        {
            return new PaymentLookup.Unavailable(answer.Message);
        }

        var payments = answer.Data.ArrayAt("items").ToList();
        if (payments.Count == 0)
        {
            return new PaymentLookup.NotFound();
        }

        var chosen = payments.FirstOrDefault(payment => payment.TextAt("paymentStatus") == "SUCCESS") is { ValueKind: JsonValueKind.Object } succeeded
            ? succeeded
            : payments.FirstOrDefault(payment => Pending.Contains(payment.TextAt("paymentStatus"))) is { ValueKind: JsonValueKind.Object } waiting ? waiting : payments[0];
        return new PaymentLookup.Found(Outcome(chosen));
    }

    /// <summary>
    /// The whole payment through <c>/refunds</c>, which cancels it on its day; part of it through the transaction, which
    /// Craftgate allows from the next day (§3.7).
    /// </summary>
    public async Task<RefundOutcome> RefundAsync(ProviderContext context, RefundRequest request, CancellationToken cancellationToken)
    {
        var full = request.Amount.MinorUnits == request.PaymentAmount.MinorUnits;
        if (!long.TryParse(request.ProviderPaymentId, NumberStyles.None, CultureInfo.InvariantCulture, out var paymentId))
        {
            return Refused("invalid_request", "The payment has no Craftgate payment ID.");
        }

        JsonObject body;
        string path;
        if (full)
        {
            path = "payment/v1/refunds";
            body = new JsonObject { ["paymentId"] = paymentId, ["conversationId"] = request.Reference, ["refundDestinationType"] = "PROVIDER" };
        }
        else if (long.TryParse(request.PaymentProviderData.GetValueOrDefault(TransactionData), NumberStyles.None, CultureInfo.InvariantCulture, out var transactionId))
        {
            path = "payment/v1/refund-transactions";
            body = new JsonObject
            {
                ["paymentTransactionId"] = transactionId,
                ["conversationId"] = request.Reference,
                ["refundPrice"] = CraftgateApi.Price(request.Amount),
                ["refundDestinationType"] = "PROVIDER",
            };
        }
        else
        {
            return Refused("invalid_request", "Part of this payment cannot be refunded: its Craftgate transaction is not known.");
        }

        using var answer = await CraftgateApi.SendAsync(context, HttpMethod.Post, path, body, CraftgateApi.PaymentTimeout, cancellationToken, request.Reference);
        if (answer.Unanswered)
        {
            return RefundOutcome.Unknown(answer.Message);
        }

        if (!answer.Succeeded)
        {
            return answer.ErrorGroup == "REQUIRES_DAY_END" || answer.ErrorDescription?.Contains("REQUIRES_DAY_END", StringComparison.Ordinal) == true
                ? Refused("refund.not_allowed_yet", answer.Message)
                : Refused(answer.ErrorGroup ?? answer.ErrorCode ?? "provider_error", answer.Message);
        }

        return RefundOutcomeOf(answer.Data);

        static RefundOutcome Refused(string code, string? message) => new() { Status = RefundStatus.Failed, FailureCode = code, FailureMessage = message };
    }

    /// <summary>Refunds, then refund transactions, by Tahsil's refund reference (§3.8).</summary>
    public async Task<RefundLookup> FindRefundAsync(ProviderContext context, RefundQuery query, CancellationToken cancellationToken)
    {
        foreach (var path in new[] { "payment-reporting/v1/refunds", "payment-reporting/v1/refund-transactions" })
        {
            using var answer = await CraftgateApi.SendAsync(
                context, HttpMethod.Get, $"{path}?{CraftgateApi.Query(("conversationId", query.Reference))}", null, CraftgateApi.LookupTimeout, cancellationToken);
            if (!answer.Succeeded)
            {
                return new RefundLookup.Unavailable(answer.Message);
            }

            if (answer.Data.ArrayAt("items").FirstOrDefault() is { ValueKind: JsonValueKind.Object } refund)
            {
                return new RefundLookup.Found(RefundOutcomeOf(refund));
            }
        }

        return new RefundLookup.NotFound();
    }

    /// <summary>Deletes the stored card (§3.9); one Craftgate no longer has counts as removed.</summary>
    public async Task<RemoveCardResult> RemoveSavedCardAsync(ProviderContext context, SavedCardReference card, CancellationToken cancellationToken)
    {
        if (Card(card.Token) is not { } stored)
        {
            return new RemoveCardResult(true);
        }

        using var answer = await CraftgateApi.SendAsync(
            context, HttpMethod.Post, "payment/v1/cards/delete", new JsonObject { ["cardUserKey"] = stored.UserKey, ["cardToken"] = stored.Token }, CraftgateApi.LookupTimeout, cancellationToken);
        return answer.Succeeded || answer.Status == 404
            ? new RemoveCardResult(true)
            : new RemoveCardResult(false, answer.Message);
    }

    /// <summary>
    /// Verifies <c>x-cg-signature-v1</c> (CRG-12). The signature does not cover the payload, so verified events are
    /// hints: the payment or refund they name is read from Craftgate for Tahsil's reference. When it cannot be read now,
    /// the event is answered with a 503 and not kept, so that Craftgate sends it again.
    /// </summary>
    public async Task<NotificationResult> HandleNotificationAsync(ProviderContext context, IncomingNotification notification, CancellationToken cancellationToken)
    {
        var body = LenientJson.TryParse(System.Text.Encoding.UTF8.GetString(notification.Body));
        using (body)
        {
            var root = body?.RootElement ?? default;
            var eventType = body is null ? null : root.TextAt("eventType");
            var timestamp = body is null ? null : root.TextAt("eventTimestamp");
            var status = body is null ? null : root.TextAt("status");
            var payloadId = body is null ? null : root.TextAt("payloadId");
            if (eventType is null || timestamp is null || status is null || payloadId is null
                || !Hmac.Matches(CraftgateSigning.Webhook(context.Credential("webhookKey"), eventType, timestamp, status, payloadId), notification.Headers.GetValueOrDefault("x-cg-signature-v1")))
            {
                return NotificationResult.Rejected(new NotificationResponse(400, "text/plain", string.Empty));
            }

            var result = new NotificationResult { Verified = true, ProviderEventId = $"{eventType}:{payloadId}:{timestamp}", EventType = eventType, Response = NotificationResponse.Ok };
            var path = eventType switch
            {
                "CHECKOUTFORM_AUTH" => $"payment/v1/checkout-payments/{Uri.EscapeDataString(payloadId)}",
                "API_AUTH" => $"payment/v1/card-payments/{Uri.EscapeDataString(payloadId)}",
                "REFUND" => $"payment/v1/refunds/{Uri.EscapeDataString(payloadId)}",
                "REFUND_TX" => $"payment/v1/refund-transactions/{Uri.EscapeDataString(payloadId)}",
                _ => null,
            };
            if (path is null)
            {
                return result;
            }

            using var answer = await CraftgateApi.SendAsync(context, HttpMethod.Get, path, null, CraftgateApi.LookupTimeout, cancellationToken);
            if (!answer.Succeeded)
            {
                return answer.Status == 404 ? result : result with { Deferred = true, Response = new NotificationResponse(503, "text/plain", string.Empty) };
            }

            if (answer.Data.TextAt("conversationId") is not { } reference)
            {
                return result;
            }

            NotificationUpdate update = eventType.StartsWith("REFUND", StringComparison.Ordinal) ? new NotificationUpdate.CheckRefund(reference) : new NotificationUpdate.CheckPayment(reference);
            return result with { Updates = [update] };
        }
    }

    private static JsonObject Item(string name, JsonNode price, string externalId) =>
        new() { ["name"] = name, ["price"] = price.DeepClone(), ["externalId"] = externalId };

    /// <summary>A payment's outcome (CRG-7, CRG-9): what Craftgate charged, or its mapped error.</summary>
    private static PaymentOutcome Outcome(JsonElement payment)
    {
        var id = payment.TextAt("id");
        var status = payment.TextAt("paymentStatus");
        if (status == "SUCCESS")
        {
            var data = new Dictionary<string, string>();
            if (payment.ArrayAt("paymentTransactions").FirstOrDefault().TextAt("id") is { } transaction)
            {
                data[TransactionData] = transaction;
            }

            var currency = payment.TextAt("currency") ?? "TRY";
            return new PaymentOutcome
            {
                Status = PaymentStatus.Succeeded,
                ProviderPaymentId = id,
                ChargedAmount = payment.DecimalAt("paidPrice") is { } paid && AmountFormat.ToMinorUnits(paid, 2) is { } minor ? new Amount(minor, currency, 2) : null,
                ProviderData = data,
            };
        }

        if (status is not null && Pending.Contains(status))
        {
            return new PaymentOutcome { Status = PaymentStatus.Pending, ProviderPaymentId = id };
        }

        var group = payment.TextAt("paymentError", "errorGroup");
        var code = payment.TextAt("paymentError", "errorCode");
        return PaymentOutcome.Failure(CraftgateFailures.Map(group, code), group ?? code, payment.TextAt("paymentError", "errorDescription")) with { ProviderPaymentId = id };
    }

    /// <summary>The card the checkout stored (CRG-8), with its expiry from the card search, best effort.</summary>
    private static async Task<SavedCard?> SavedCardAsync(ProviderContext context, JsonElement payment, string? providerCustomer, CancellationToken cancellationToken)
    {
        var userKey = payment.TextAt("cardUserKey") ?? providerCustomer;
        if (userKey is null)
        {
            return null;
        }

        var token = payment.TextAt("cardToken");
        var query = token is null ? CraftgateApi.Query(("cardUserKey", userKey)) : CraftgateApi.Query(("cardUserKey", userKey), ("cardToken", token));
        using var cards = await CraftgateApi.SendAsync(context, HttpMethod.Get, $"payment/v1/cards?{query}", null, CraftgateApi.LookupTimeout, cancellationToken);
        var bin = payment.TextAt("binNumber");
        var last4 = payment.TextAt("lastFourDigits");

        // Paid with a card stored before: no token comes back, and the search by BIN and last four digits finds it.
        var stored = cards.Succeeded
            ? cards.Data.ArrayAt("items").FirstOrDefault(card => token is null ? card.TextAt("binNumber") == bin && card.TextAt("lastFourDigits") == last4 : card.TextAt("cardToken") == token)
            : default;
        token ??= stored.ValueKind == JsonValueKind.Object ? stored.TextAt("cardToken") : null;
        if (token is null)
        {
            return null;
        }

        return new SavedCard
        {
            ProviderCustomer = userKey,
            Token = new JsonObject { ["cardUserKey"] = userKey, ["cardToken"] = token }.ToJsonString(),
            Bin = bin,
            Last4 = last4,
            Brand = payment.TextAt("cardAssociation") switch
            {
                "VISA" => "visa",
                "MASTER_CARD" => "mastercard",
                "AMEX" => "amex",
                "TROY" => "troy",
                "JCB" => "jcb",
                "UNION_PAY" => "unionpay",
                "DISCOVER" => "discover",
                _ => "other",
            },
            Funding = payment.TextAt("cardType")?.ToLowerInvariant().Replace("_card", string.Empty, StringComparison.Ordinal),
            Issuer = payment.TextAt("cardIssuerBankName"),
            CardFamily = payment.TextAt("cardBrand"),
            ExpMonth = stored.ValueKind == JsonValueKind.Object && int.TryParse(stored.TextAt("expireMonth"), CultureInfo.InvariantCulture, out var month) ? month : null,
            ExpYear = stored.ValueKind == JsonValueKind.Object && int.TryParse(stored.TextAt("expireYear"), CultureInfo.InvariantCulture, out var year) ? year : null,
        };
    }

    private static RefundOutcome RefundOutcomeOf(JsonElement refund) => new()
    {
        Status = refund.TextAt("status") switch
        {
            "SUCCESS" => RefundStatus.Succeeded,
            "FAILURE" => RefundStatus.Failed,
            _ => RefundStatus.Pending,
        },
        Kind = refund.TextAt("refundType") == "CANCEL" ? RefundKind.Void : RefundKind.Refund,
        ProviderRefundId = refund.TextAt("id"),
    };

    private static (string UserKey, string Token)? Card(string token)
    {
        try
        {
            return JsonNode.Parse(token) is { } node && node["cardUserKey"]?.GetValue<string>() is { } userKey && node["cardToken"]?.GetValue<string>() is { } cardToken
                ? (userKey, cardToken)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
