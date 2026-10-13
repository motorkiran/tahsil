using System.Text.Json;
using System.Text.Json.Nodes;
using Stripe;
using Stripe.Checkout;
using Tahsil.Providers.Abstractions;
using PaymentMethod = Stripe.PaymentMethod;

namespace Tahsil.Providers.Stripe;

/// <summary>
/// Stripe (docs/specs/providers/stripe.md) as a payment rail, never Stripe Billing: customers pay or save a card on
/// Stripe Checkout, renewals are off-session PaymentIntents, refunds may settle later, and thin events are verified
/// and read through the objects they name.
/// </summary>
public sealed class StripeProvider : IPaymentProvider
{
    public const string Key = "stripe";

    private const string WebhookSecret = "webhookSigningSecret";
    private const string DescriptorSuffix = "statementDescriptorSuffix";
    private const string SessionData = "checkoutSessionId";
    private const string FingerprintData = "fingerprint";

    /// <summary>Stripe's shortest Checkout Session, with a minute's margin for clocks.</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(31);

    private static readonly Dictionary<string, long> Minimums = new(StringComparer.Ordinal)
    {
        ["USD"] = 50,
        ["EUR"] = 50,
        ["GBP"] = 30,
        ["CHF"] = 50,
        ["CAD"] = 50,
        ["AUD"] = 50,
        ["JPY"] = 50,
    };

    public ProviderDescriptor Descriptor { get; } = new()
    {
        Key = Key,
        DisplayName = "Stripe",
        Credentials =
        [
            new FieldDefinition
            {
                Key = StripeCalls.SecretKey,
                Label = "Secret or restricted key",
                Type = FieldType.Secret,
                Pattern = "^(sk|rk)_(test|live)_",
                Description = "A restricted key (rk_…) with write access to Customers, Payment Intents, Setup Intents, Payment Methods, Checkout Sessions, Charges and Refunds, and read access to Events.",
            },
            new FieldDefinition
            {
                Key = WebhookSecret,
                Label = "Event destination signing secret",
                Type = FieldType.Secret,
                Pattern = "^whsec_",
                Description = "Of the event destination with thin payloads for this connection's notification URL.",
            },
        ],
        Settings =
        [
            new FieldDefinition
            {
                Key = DescriptorSuffix,
                Label = "Statement descriptor suffix",
                Required = false,
                Pattern = "^[A-Za-z0-9 ]{1,22}$",
                Description = "Added to the prefix set in Stripe's Dashboard on card statements.",
            },
        ],
        Currencies = new HashSet<string>(Minimums.Keys, StringComparer.Ordinal),
        MinimumAmounts = Minimums,
        Capabilities = new ProviderCapabilities
        {
            Entry = new HashSet<CardEntry> { CardEntry.Redirect },
            SavesCards = CardSaving.DuringPayment,
            Setup = CardSetup.Native,
            ChargesSavedCards = true,
            Refunds = RefundSupport.Partial,
            AsynchronousRefunds = true,
            Voids = VoidSupport.None,
            FindByReference = ReferenceSearch.Any,
            Notifications = NotificationSupport.Verifiable,
            IdempotentCharges = true,
        },
        CustomerData = CustomerDataRequirements.None,
        ContentSecurity = new Dictionary<ProviderEnvironment, ContentSecuritySources>
        {
            [ProviderEnvironment.Sandbox] = ContentSecuritySources.None,
            [ProviderEnvironment.Production] = ContentSecuritySources.None,
        },
    };

    /// <summary>A customer list of one proves the key, its mode and its permissions (§3.2).</summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var live = context.Credential(StripeCalls.SecretKey).Contains("_live_", StringComparison.Ordinal);
        if (live != (context.Environment == ProviderEnvironment.Production))
        {
            return new ConnectionTestResult(false, "livemode_mismatch", "The key's mode does not match the connection's environment: test keys go with sandbox connections, live keys with production ones.");
        }

        var client = StripeCalls.Client(context);
        var answer = await StripeCalls.CallAsync(
            token => client.V1.Customers.ListAsync(new CustomerListOptions { Limit = 1 }, cancellationToken: token), ProviderHttp.LookupTimeout, cancellationToken);
        return answer switch
        {
            { Succeeded: true } => ConnectionTestResult.Success,
            { Unanswered: true } => new ConnectionTestResult(false, "unreachable", "Stripe did not answer; try again later."),
            _ => new ConnectionTestResult(false, "invalid_credentials", answer.Message),
        };
    }

    /// <summary>Creates the customer if needed (STR-4) and a Checkout Session for the payment or setup (STR-5).</summary>
    public async Task<StartResult> StartAsync(ProviderContext context, StartRequest request, CancellationToken cancellationToken)
    {
        var client = StripeCalls.Client(context);
        var customer = request.ProviderCustomer;
        string? created = null;
        if (customer is null)
        {
            var made = await StripeCalls.CallAsync(
                token => client.V1.Customers.CreateAsync(
                    new CustomerCreateOptions
                    {
                        Email = request.Customer.Email,
                        Name = request.Customer.Name,
                        Metadata = new Dictionary<string, string> { ["tahsil_customer_id"] = request.Customer.Id.ToString() },
                    },
                    StripeCalls.Idempotent(context, "customer", request.Customer.Id.ToString()),
                    token),
                ProviderHttp.ChangeTimeout,
                cancellationToken);
            if (!made.Succeeded)
            {
                return StartResult.Failed(PaymentOutcome.Failure(made.Failure, made.Error?.StripeError?.Code, made.Message));
            }

            customer = created = made.Value!.Id;
        }

        var metadata = new Dictionary<string, string> { ["tahsil_payment_id"] = request.PaymentId.ToString(), ["tahsil_reference"] = request.Reference };
        var setup = request.Purpose == PaymentPurpose.Setup;
        var options = new SessionCreateOptions
        {
            Mode = setup ? "setup" : "payment",
            UiMode = "hosted_page",
            Customer = customer,
            AllowedPaymentMethodTypes = ["card"],
            ClientReferenceId = request.Reference,
            SuccessUrl = $"{request.ReturnUrl.AbsoluteUri}{(string.IsNullOrEmpty(request.ReturnUrl.Query) ? '?' : '&')}session_id={{CHECKOUT_SESSION_ID}}",
            CancelUrl = request.CancelUrl.AbsoluteUri,
            Locale = request.Locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en",
            ExpiresAt = (context.Time.GetUtcNow() + SessionLifetime).UtcDateTime,
        };
        if (setup)
        {
            options.Currency = Currency(request.Amount);
            options.SetupIntentData = new SessionSetupIntentDataOptions { Metadata = metadata };
        }
        else
        {
            // One line with Tahsil's total, so Stripe never recomputes amounts or tax (STR-5).
            options.LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = Currency(request.Amount),
                        UnitAmount = request.Amount.MinorUnits,
                        ProductData = new SessionLineItemPriceDataProductDataOptions { Name = request.Description ?? (request.Lines.Count > 0 ? request.Lines[0].Description : "Payment") },
                    },
                },
            ];
            options.PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                SetupFutureUsage = request.SaveCard ? "off_session" : null,
                Metadata = metadata,
                StatementDescriptorSuffix = context.Setting(DescriptorSuffix),
            };
        }

        var session = await StripeCalls.CallAsync(
            token => client.V1.Checkout.Sessions.CreateAsync(options, StripeCalls.Idempotent(context, "checkout", request.Reference), token),
            ProviderHttp.ChangeTimeout,
            cancellationToken);
        if (!session.Succeeded)
        {
            // Creating a session charges nothing; the customer may try again.
            return StartResult.Failed(PaymentOutcome.Failure(session.Failure, session.Error?.StripeError?.Code, session.Message)) with { ProviderCustomer = created };
        }

        return new StartResult
        {
            Presentation = new Presentation.Redirect(new Uri(session.Value!.Url)),
            ProviderData = new Dictionary<string, string> { [SessionData] = session.Value.Id },
            ProviderCustomer = created,
            ExpiresAt = session.Value.ExpiresAt,
        };
    }

    /// <summary>Reads the session Tahsil created, never the one the return URL names (STR-6), with the card it saved (STR-7).</summary>
    public async Task<PaymentOutcome> CompleteAsync(ProviderContext context, CompleteRequest request, CancellationToken cancellationToken)
    {
        if (!request.ProviderData.TryGetValue(SessionData, out var sessionId))
        {
            return PaymentOutcome.Failure(FailureCode.InvalidRequest, providerMessage: "The payment has no Checkout Session.");
        }

        var answer = await SessionAsync(context, sessionId, cancellationToken);
        return answer.Succeeded ? Outcome(answer.Value!) : PaymentOutcome.Unknown(answer.Message);
    }

    /// <summary>An off-session PaymentIntent with the saved card (STR-8).</summary>
    public async Task<PaymentOutcome> ChargeSavedCardAsync(ProviderContext context, ChargeRequest request, CancellationToken cancellationToken)
    {
        if (ReadToken(request.SavedCard.Token) is not { } paymentMethod)
        {
            return PaymentOutcome.Failure(FailureCode.PaymentMethodMissing, providerMessage: "The saved card's token is not a Stripe payment method.");
        }

        var client = StripeCalls.Client(context);
        var answer = await StripeCalls.CallAsync(
            token => client.V1.PaymentIntents.CreateAsync(
                new PaymentIntentCreateOptions
                {
                    Amount = request.Amount.MinorUnits,
                    Currency = Currency(request.Amount),
                    Customer = request.SavedCard.ProviderCustomer,
                    PaymentMethod = paymentMethod,
                    OffSession = true,
                    Confirm = true,
                    AllowedPaymentMethodTypes = ["card"],
                    Description = request.Description,
                    StatementDescriptorSuffix = context.Setting(DescriptorSuffix),
                    Metadata = new Dictionary<string, string> { ["tahsil_payment_id"] = request.PaymentId.ToString(), ["tahsil_reference"] = request.Reference },
                },
                StripeCalls.Idempotent(context, "charge", request.Reference),
                token),
            ProviderHttp.ChangeTimeout,
            cancellationToken);
        if (answer.Succeeded)
        {
            return Outcome(answer.Value!);
        }

        if (answer.Unanswered || StripeFailures.IsUncertain(answer.Failure))
        {
            return PaymentOutcome.Unknown(answer.Message);
        }

        var error = answer.Error!.StripeError;
        var intent = error?.PaymentIntent?.Id;
        if (answer.Failure == FailureCode.AuthenticationRequired && intent is not null)
        {
            // The customer pays on the hosted invoice page instead; the intent left waiting would only confuse lookups.
            await StripeCalls.CallAsync(
                token => client.V1.PaymentIntents.CancelAsync(intent, null, StripeCalls.Idempotent(context, "cancel", intent), token), ProviderHttp.ChangeTimeout, cancellationToken);
        }

        return PaymentOutcome.Failure(answer.Failure, error?.DeclineCode ?? error?.Code, answer.Message) with { ProviderPaymentId = intent };
    }

    /// <summary>By the known PaymentIntent, else the Checkout Session, else the customer's recent intents by reference (STR-9).</summary>
    public async Task<PaymentLookup> FindPaymentAsync(ProviderContext context, PaymentQuery query, CancellationToken cancellationToken)
    {
        var client = StripeCalls.Client(context);
        if (query.ProviderPaymentId is { } id && id.StartsWith("pi_", StringComparison.Ordinal))
        {
            var intent = await StripeCalls.CallAsync(
                token => client.V1.PaymentIntents.GetAsync(id, cancellationToken: token), ProviderHttp.LookupTimeout, cancellationToken);
            return intent.Succeeded ? Found(Outcome(intent.Value!)) : Missing(intent);
        }

        if (query.ProviderData.TryGetValue(SessionData, out var sessionId))
        {
            var session = await SessionAsync(context, sessionId, cancellationToken);
            return session.Succeeded
                ? session.Value!.Status == "open" && session.Value.PaymentIntentId is null && session.Value.SetupIntentId is null ? new PaymentLookup.NotFound() : Found(Outcome(session.Value))
                : Missing(session);
        }

        if (query.ProviderCustomer is not { } customer)
        {
            return new PaymentLookup.NotFound();
        }

        // Lists are consistent at once, unlike Search.
        var listed = await StripeCalls.CallAsync(
            token => client.V1.PaymentIntents.ListAsync(
                new PaymentIntentListOptions { Customer = customer, Created = new DateRangeOptions { GreaterThanOrEqual = query.CreatedAt.AddHours(-1).UtcDateTime }, Limit = 100 },
                cancellationToken: token),
            ProviderHttp.LookupTimeout,
            cancellationToken);
        if (!listed.Succeeded)
        {
            return new PaymentLookup.Unavailable(listed.Message);
        }

        return listed.Value!.Data.FirstOrDefault(intent => intent.Metadata.GetValueOrDefault("tahsil_reference") == query.Reference) is { } match
            ? Found(Outcome(match))
            : new PaymentLookup.NotFound();
    }

    /// <summary>A refund of the PaymentIntent (STR-10); Stripe may finish it later.</summary>
    public async Task<RefundOutcome> RefundAsync(ProviderContext context, RefundRequest request, CancellationToken cancellationToken)
    {
        if (request.ProviderPaymentId is not { } intent || !intent.StartsWith("pi_", StringComparison.Ordinal))
        {
            return new RefundOutcome { Status = RefundStatus.Failed, FailureCode = "invalid_request", FailureMessage = "Only payments made through a PaymentIntent can be refunded." };
        }

        var client = StripeCalls.Client(context);
        var answer = await StripeCalls.CallAsync(
            token => client.V1.Refunds.CreateAsync(
                new RefundCreateOptions
                {
                    PaymentIntent = intent,
                    Amount = request.Amount.MinorUnits,
                    Reason = request.Reason switch
                    {
                        "duplicate" or "late_success" => "duplicate",
                        "fraudulent" => "fraudulent",
                        _ => "requested_by_customer",
                    },
                    Metadata = new Dictionary<string, string> { ["tahsil_refund_id"] = request.RefundId.ToString(), ["tahsil_reference"] = request.Reference },
                },
                StripeCalls.Idempotent(context, "refund", request.Reference),
                token),
            ProviderHttp.ChangeTimeout,
            cancellationToken);
        if (answer.Succeeded)
        {
            return RefundOutcomeOf(answer.Value!);
        }

        return answer.Unanswered || StripeFailures.IsUncertain(answer.Failure)
            ? RefundOutcome.Unknown(answer.Message)
            : new RefundOutcome { Status = RefundStatus.Failed, FailureCode = answer.Error?.StripeError?.Code ?? "provider_error", FailureMessage = answer.Message };
    }

    /// <summary>By ID, or among the PaymentIntent's refunds by Tahsil's reference (§3.8).</summary>
    public async Task<RefundLookup> FindRefundAsync(ProviderContext context, RefundQuery query, CancellationToken cancellationToken)
    {
        var client = StripeCalls.Client(context);
        if (query.ProviderRefundId is { } id)
        {
            var refund = await StripeCalls.CallAsync(token => client.V1.Refunds.GetAsync(id, cancellationToken: token), ProviderHttp.LookupTimeout, cancellationToken);
            return refund.Succeeded ? new RefundLookup.Found(RefundOutcomeOf(refund.Value!)) : MissingRefund(refund);
        }

        if (query.ProviderPaymentId is not { } intent)
        {
            return new RefundLookup.NotFound();
        }

        var listed = await StripeCalls.CallAsync(
            token => client.V1.Refunds.ListAsync(new RefundListOptions { PaymentIntent = intent, Limit = 100 }, cancellationToken: token), ProviderHttp.LookupTimeout, cancellationToken);
        if (!listed.Succeeded)
        {
            return MissingRefund(listed);
        }

        return listed.Value!.Data.FirstOrDefault(refund => refund.Metadata.GetValueOrDefault("tahsil_reference") == query.Reference) is { } match
            ? new RefundLookup.Found(RefundOutcomeOf(match))
            : new RefundLookup.NotFound();
    }

    /// <summary>Detaches the payment method, for good (§3.9); one already detached or gone counts as removed.</summary>
    public async Task<RemoveCardResult> RemoveSavedCardAsync(ProviderContext context, SavedCardReference card, CancellationToken cancellationToken)
    {
        if (ReadToken(card.Token) is not { } paymentMethod)
        {
            return new RemoveCardResult(true);
        }

        var client = StripeCalls.Client(context);
        var answer = await StripeCalls.CallAsync(
            token => client.V1.PaymentMethods.DetachAsync(paymentMethod, null, StripeCalls.Idempotent(context, "detach", paymentMethod), token),
            ProviderHttp.ChangeTimeout,
            cancellationToken);
        return answer.Succeeded || answer.Error?.StripeError?.Code is "resource_missing" or "payment_method_unexpected_state"
            ? new RemoveCardResult(true)
            : new RemoveCardResult(false, answer.Message);
    }

    /// <summary>
    /// Verifies a thin event (STR-11) and reads the object it names for Tahsil's reference (STR-12). When that object
    /// cannot be read now, the event is answered with a 503 and not kept, so that Stripe sends it again.
    /// </summary>
    public async Task<NotificationResult> HandleNotificationAsync(ProviderContext context, IncomingNotification notification, CancellationToken cancellationToken)
    {
        if (!StripeSignature.IsValid(notification.Headers.GetValueOrDefault("Stripe-Signature"), notification.Body, context.Credential(WebhookSecret), context.Time.GetUtcNow()))
        {
            return NotificationResult.Rejected(new NotificationResponse(400, "text/plain", string.Empty));
        }

        ThinEvent? received;
        try
        {
            received = JsonSerializer.Deserialize<ThinEvent>(notification.Body, ThinEvent.Json);
        }
        catch (JsonException)
        {
            received = null;
        }

        if (received is not { Id: { } eventId, Type: { } type })
        {
            return NotificationResult.Rejected(new NotificationResponse(400, "text/plain", string.Empty));
        }

        var result = new NotificationResult { Verified = true, ProviderEventId = eventId, EventType = type, Response = NotificationResponse.Ok };
        if (received.RelatedObject?.Id is not { } objectId)
        {
            return result;
        }

        var update = await UpdateAsync(context, type, objectId, cancellationToken);
        return update switch
        {
            null => result,
            { Deferred: true } => result with { Deferred = true, Response = new NotificationResponse(503, "text/plain", string.Empty) },
            { } read => result with { Updates = [read.Update!] },
        };
    }

    /// <summary>What a thin event means, from the object it names; <see langword="null"/> for events Tahsil ignores.</summary>
    private static async Task<(NotificationUpdate? Update, bool Deferred)?> UpdateAsync(ProviderContext context, string type, string objectId, CancellationToken cancellationToken)
    {
        var client = StripeCalls.Client(context);
        switch (type)
        {
            case "v1.checkout.session.completed" or "v1.checkout.session.expired":
                var session = await StripeCalls.CallAsync(token => client.V1.Checkout.Sessions.GetAsync(objectId, cancellationToken: token), ProviderHttp.LookupTimeout, cancellationToken);
                return session.Succeeded ? Hint(session.Value!.ClientReferenceId) : (null, true);
            case "v1.payment_intent.succeeded" or "v1.payment_intent.payment_failed" or "v1.payment_intent.canceled":
                var intent = await StripeCalls.CallAsync(token => client.V1.PaymentIntents.GetAsync(objectId, cancellationToken: token), ProviderHttp.LookupTimeout, cancellationToken);
                return intent.Succeeded ? Hint(intent.Value!.Metadata.GetValueOrDefault("tahsil_reference")) : (null, true);
            case "v1.setup_intent.succeeded" or "v1.setup_intent.setup_failed":
                var setup = await StripeCalls.CallAsync(token => client.V1.SetupIntents.GetAsync(objectId, cancellationToken: token), ProviderHttp.LookupTimeout, cancellationToken);
                return setup.Succeeded ? Hint(setup.Value!.Metadata.GetValueOrDefault("tahsil_reference")) : (null, true);
            case "v1.refund.created" or "v1.refund.updated" or "v1.refund.failed":
                var refund = await StripeCalls.CallAsync(token => client.V1.Refunds.GetAsync(objectId, cancellationToken: token), ProviderHttp.LookupTimeout, cancellationToken);
                return refund.Succeeded
                    ? refund.Value!.Metadata.GetValueOrDefault("tahsil_reference") is { } reference ? (new NotificationUpdate.CheckRefund(reference), false) : null
                    : (null, true);
            case "v1.payment_method.automatically_updated":
                var method = await StripeCalls.CallAsync(token => client.V1.PaymentMethods.GetAsync(objectId, cancellationToken: token), ProviderHttp.LookupTimeout, cancellationToken);
                return method.Succeeded
                    ? method.Value!.Card is null ? null : (new NotificationUpdate.CardUpdated(Token(method.Value.Id), Card(method.Value, method.Value.CustomerId)), false)
                    : (null, true);
            default:
                // Unknown types are acknowledged.
                return null;
        }

        static (NotificationUpdate? Update, bool Deferred)? Hint(string? reference) =>
            reference is null ? null : (new NotificationUpdate.CheckPayment(reference), false);
    }

    private static Task<StripeAnswer<Session>> SessionAsync(ProviderContext context, string sessionId, CancellationToken cancellationToken)
    {
        var client = StripeCalls.Client(context);
        return StripeCalls.CallAsync(
            token => client.V1.Checkout.Sessions.GetAsync(
                sessionId, new SessionGetOptions { Expand = ["payment_intent.payment_method", "setup_intent.payment_method"] }, cancellationToken: token),
            ProviderHttp.LookupTimeout,
            cancellationToken);
    }

    /// <summary>A Checkout Session's outcome (STR-6).</summary>
    private static PaymentOutcome Outcome(Session session)
    {
        if (session.Status == "expired")
        {
            return PaymentOutcome.Failure(FailureCode.Abandoned, "expired", "The Checkout Session expired.");
        }

        if (session.Mode == "setup")
        {
            return session.SetupIntent is { } setup ? Outcome(setup, session.CustomerId) : new PaymentOutcome { Status = PaymentStatus.Pending };
        }

        var outcome = session.PaymentIntent is { } intent ? Outcome(intent) : new PaymentOutcome { Status = PaymentStatus.Pending };
        return outcome.Status == PaymentStatus.Succeeded && session.Status != "complete" ? outcome with { Status = PaymentStatus.Pending } : outcome;
    }

    /// <summary>A PaymentIntent's outcome, with the card it saved for later when it was asked to.</summary>
    private static PaymentOutcome Outcome(PaymentIntent intent)
    {
        var card = intent.SetupFutureUsage == "off_session" && intent.PaymentMethod is { Card: not null } method ? Card(method, intent.CustomerId) : null;
        return intent.Status switch
        {
            "succeeded" => new PaymentOutcome
            {
                Status = PaymentStatus.Succeeded,
                ProviderPaymentId = intent.Id,
                ChargedAmount = AmountOf(intent.AmountReceived, intent.Currency),
                SavedCard = card,
                ProviderData = Fingerprint(intent.PaymentMethod),
            },
            "processing" or "requires_confirmation" => new PaymentOutcome { Status = PaymentStatus.Pending, ProviderPaymentId = intent.Id },
            "requires_action" => new PaymentOutcome { Status = PaymentStatus.RequiresAction, ProviderPaymentId = intent.Id },
            "canceled" => PaymentOutcome.Failure(FailureCode.Abandoned, "canceled", intent.CancellationReason) with { ProviderPaymentId = intent.Id },
            _ when intent.LastPaymentError is { } error => PaymentOutcome.Failure(
                StripeFailures.Map(error.DeclineCode, error.Code, error.Type), error.DeclineCode ?? error.Code, error.Message) with
            { ProviderPaymentId = intent.Id },
            _ => new PaymentOutcome { Status = PaymentStatus.Pending, ProviderPaymentId = intent.Id },
        };
    }

    /// <summary>A SetupIntent's outcome: the card saved without a charge.</summary>
    private static PaymentOutcome Outcome(SetupIntent setup, string? customer) => setup.Status switch
    {
        "succeeded" when setup.PaymentMethod is { Card: not null } method => new PaymentOutcome
        {
            Status = PaymentStatus.Succeeded,
            ProviderPaymentId = setup.Id,
            SavedCard = Card(method, customer ?? setup.CustomerId),
            ProviderData = Fingerprint(method),
        },
        "processing" => new PaymentOutcome { Status = PaymentStatus.Pending, ProviderPaymentId = setup.Id },
        "canceled" => PaymentOutcome.Failure(FailureCode.Abandoned, "canceled") with { ProviderPaymentId = setup.Id },
        _ when setup.LastSetupError is { } error => PaymentOutcome.Failure(
            StripeFailures.Map(error.DeclineCode, error.Code, error.Type), error.DeclineCode ?? error.Code, error.Message) with
        { ProviderPaymentId = setup.Id },
        _ => new PaymentOutcome { Status = PaymentStatus.Pending, ProviderPaymentId = setup.Id },
    };

    private static RefundOutcome RefundOutcomeOf(Refund refund) => new()
    {
        Status = refund.Status switch
        {
            "succeeded" => RefundStatus.Succeeded,
            "failed" or "canceled" => RefundStatus.Failed,
            _ => RefundStatus.Pending,
        },
        ProviderRefundId = refund.Id,
        FailureCode = refund.FailureReason,
        FailureMessage = refund.FailureReason is null ? null : $"Stripe could not refund: {refund.FailureReason}.",
    };

    /// <summary>The saved card (STR-7): its token is the payment method, with the customer it is attached to.</summary>
    private static SavedCard Card(PaymentMethod method, string? customer) => new()
    {
        ProviderCustomer = customer,
        Token = Token(method.Id),
        Brand = method.Card.Brand,
        Funding = method.Card.Funding,
        Last4 = method.Card.Last4,
        ExpMonth = (int)method.Card.ExpMonth,
        ExpYear = (int)method.Card.ExpYear,
        Country = method.Card.Country,
    };

    private static Dictionary<string, string> Fingerprint(PaymentMethod? method) =>
        method?.Card?.Fingerprint is { } fingerprint ? new() { [FingerprintData] = fingerprint } : [];

    private static string Token(string paymentMethodId) => new JsonObject { ["paymentMethodId"] = paymentMethodId }.ToJsonString();

    private static string? ReadToken(string token)
    {
        try
        {
            return JsonNode.Parse(token)?["paymentMethodId"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Tahsil's minor units are Stripe's amounts for the MVP's currencies (STR-13); codes go in lowercase.</summary>
    private static string Currency(Amount amount) => amount.Currency.ToLowerInvariant();

    private static Amount AmountOf(long minorUnits, string currency) =>
        new(minorUnits, currency.ToUpperInvariant(), currency.Equals("jpy", StringComparison.OrdinalIgnoreCase) ? 0 : 2);

    private static PaymentLookup.Found Found(PaymentOutcome outcome) => new(outcome);

    private static PaymentLookup Missing<T>(StripeAnswer<T> answer)
        where T : class =>
        answer.Error?.StripeError?.Code == "resource_missing" ? new PaymentLookup.NotFound() : new PaymentLookup.Unavailable(answer.Message);

    private static RefundLookup MissingRefund<T>(StripeAnswer<T> answer)
        where T : class =>
        answer.Error?.StripeError?.Code == "resource_missing" ? new RefundLookup.NotFound() : new RefundLookup.Unavailable(answer.Message);

    /// <summary>A thin event (STR-12): its ID, type and the object it names.</summary>
    private sealed record ThinEvent(string? Id, string? Type, ThinEventObject? RelatedObject)
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    }

    private sealed record ThinEventObject(string? Id, string? Type, string? Url);
}
