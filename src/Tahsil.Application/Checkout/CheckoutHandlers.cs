using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Customers;
using Tahsil.Application.Invoices;
using Tahsil.Application.TestClock;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;
using Tahsil.Providers.Abstractions;
using CollectionMethod = Tahsil.Domain.Invoices.CollectionMethod;
using Model = Tahsil.Domain.Checkout;
using ModelCustomers = Tahsil.Domain.Customers;

namespace Tahsil.Application.Checkout;

/// <summary>
/// Creates a checkout session (CHK §1): the items and the customer are checked, the currency must be routed to a
/// connection that can do what the session needs, and the quote is computed now; the browser never supplies amounts.
/// </summary>
public sealed partial class CreateCheckoutSessionHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IIdempotency idempotency,
    InvoiceLines lines,
    Subscriptions.SubscriptionLines subscriptionLines,
    ProviderRegistry registry,
    CustomerMapper customers,
    CheckoutMapper mapper,
    Coupons.Redemptions redemptions,
    CheckoutDiscounts discounts,
    Kimlik.KimlikReferences kimlik,
    IOptions<CheckoutOptions> checkout,
    IOptions<BillingOptions> billing,
    IOptions<ServerOptions> server,
    BillingClock clock)
{
    public async Task<Result<CheckoutSessionResponse>> HandleAsync(CreateCheckoutSessionRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CheckoutWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var buyer = await BuyerAsync(request, cancellationToken);
        if (buyer.IsFailure)
        {
            return buyer.Error;
        }

        var (customer, details) = buyer.Value;
        if (request.Locale is { } requested && !ServerOptions.SupportedLocales.Contains(requested))
        {
            return Model.CheckoutErrors.Invalid("locale", $"The locale is one of {string.Join(", ", ServerOptions.SupportedLocales)}.");
        }

        var locale = request.Locale ?? customer?.Locale ?? details?.Locale ?? server.Value.DefaultLocale;
        var now = clock.GetUtcNow();
        if (request.Mode != CheckoutMode.Subscription && request.Subscription is not null)
        {
            return Model.CheckoutErrors.Invalid("subscription", "Only subscription sessions describe a subscription.");
        }

        var sold = request.Mode switch
        {
            CheckoutMode.Payment => await ItemsAsync(request, locale, cancellationToken),
            CheckoutMode.Subscription => await SubscriptionItemsAsync(request, locale, now, cancellationToken),
            _ => SetupCurrency(request),
        };
        if (sold.IsFailure)
        {
            return sold.Error;
        }

        var (currency, items, drafts, recurring) = sold.Value;
        var taxExempt = customer?.IsTaxExempt ?? false;
        var quote = Model.CheckoutQuote.Of(drafts, currency, taxExempt) with
        {
            Recurring = recurring is { } period
                ? new Model.CheckoutRecurring(period.Interval, period.IntervalCount, Model.CheckoutQuote.Of(period.Lines, currency, taxExempt))
                : null,
        };
        var subscription = recurring is null ? null : Subscription(request.Subscription, recurring);
        var connection = await ConnectionCheckAsync(request.Mode, currency, quote, subscription, cancellationToken);
        if (connection.IsFailure)
        {
            return connection.Error;
        }

        subscription = connection.Value;
        TimeSpan lifetime = Model.CheckoutSession.MaximumLifetime;
        if (request.ExpiresIn is { } expiresIn)
        {
            if (IsoDuration.Add(now, expiresIn, TimeZoneInfo.Utc) is not { } expiresAt)
            {
                return Model.CheckoutErrors.InvalidLifetime;
            }

            lifetime = expiresAt - now;
        }

        var token = CheckoutTokens.New();
        var created = Model.CheckoutSession.Create(
            request.Mode.Map<Model.CheckoutMode>(),
            CheckoutTokens.Hash(token),
            mapper.EncryptToken(token),
            customer?.Id,
            details,
            currency,
            items,
            quote,
            Documents(request.Mode),
            new Model.CheckoutTerms(
                request.SuccessUrl,
                request.CancelUrl,
                lifetime,
                request.AllowPromotionCodes,
                request.BillingDetailsCollection.Map<Model.BillingDetailsCollectionMode>(),
                request.Locale,
                request.ClientReferenceId,
                request.Metadata,
                server.Value.IsTestMode),
            now,
            subscription);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var session = created.Value;
        if (request.Coupon is not null || request.PromotionCode is not null)
        {
            var named = await redemptions.FindAsync(request.Coupon, request.PromotionCode, cancellationToken);
            if (named.IsFailure)
            {
                return named.Error;
            }

            if (await discounts.ApplyAsync(session, named.Value.CouponId, named.Value.CodeId, customer?.Id, taxExempt, cancellationToken) is { IsFailure: true } refused)
            {
                return refused.Error;
            }
        }

        context.CheckoutSessions.Add(session);
        idempotency.RecordCreated(ResourceTypes.CheckoutSession, session.Id);
        audit.Record(
            AuditActions.CheckoutSessionCreated,
            AuditSubject.Of(AuditSubjects.CheckoutSession, session.Id),
            new Dictionary<string, object?> { ["mode"] = request.Mode, ["currency"] = currency, ["amountDue"] = session.Quote.AmountDue, ["clientReferenceId"] = session.ClientReferenceId },
            customerId: session.CustomerId);
        await context.SaveChangesAsync(cancellationToken);
        return mapper.ToResponse(session);
    }

    /// <summary>The legal documents configured for the mode, with their current versions (CHK-11, CHK-26).</summary>
    private List<Model.CheckoutDocument> Documents(CheckoutMode mode) =>
    [
        .. checkout.Value.Documents
            .Where(document => document.AppliesTo(mode.ToString().ToLowerInvariant()))
            .Select(document => new Model.CheckoutDocument(
                document.Key,
                document.Version,
                document.Type,
                document.RequiresAcceptance,
                [.. (document.CustomerTypes.Count == 0 ? LegalDocumentOptions.AllCustomerTypes : document.CustomerTypes)
                    .Select(type => type == "company" ? ModelCustomers.CustomerType.Company : ModelCustomers.CustomerType.Individual)])),
    ];

    /// <summary>The existing customer, or the details of one to create on submission (CHK-2).</summary>
    private async Task<Result<(ModelCustomers.Customer? Customer, Model.CheckoutCustomer? Details)>> BuyerAsync(
        CreateCheckoutSessionRequest request, CancellationToken cancellationToken)
    {
        if (request.Customer is { } customerId)
        {
            if (request.CustomerDetails is not null)
            {
                return Model.CheckoutErrors.Invalid("customerDetails", "Give an existing customer or the details of a new one, not both.");
            }

            return await context.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == customerId, cancellationToken) switch
            {
                null => ModelCustomers.CustomerErrors.NotFound,
                { IsAnonymized: true } => ModelCustomers.CustomerErrors.Anonymized,
                var customer => (customer, null),
            };
        }

        if (request.CustomerDetails is not { } given)
        {
            return (null, null);
        }

        var details = customers.FromCreate(new CreateCustomerRequest
        {
            ExternalId = given.ExternalId,
            Email = given.Email,
            Name = given.Name,
            Type = given.Type,
            Phone = given.Phone,
            Address = given.Address,
            Locale = given.Locale,
            KimlikSubscriber = given.KimlikSubscriber,
        });
        if (ModelCustomers.Customer.Validate(details) is { } invalid)
        {
            return invalid with { Field = $"customerDetails.{invalid.Field}" };
        }

        if (Text.Normalize(given.ExternalId) is { Length: > ModelCustomers.Customer.ExternalIdMaxLength })
        {
            return ModelCustomers.CustomerErrors.Invalid("customerDetails.externalId", "The external ID is at most 200 characters.");
        }

        if (await kimlik.CheckSubscriberAsync(details.KimlikSubscriber, cancellationToken) is { IsFailure: true } unknown)
        {
            return unknown.Error with { Field = "customerDetails.kimlikSubscriber" };
        }

        return (null, new Model.CheckoutCustomer(
            Text.Normalize(given.ExternalId), details.Email, details.Name, details.Type, details.Phone, details.Address, given.Locale, details.KimlikSubscriber));
    }

    /// <summary>The one-time prices sold, in one currency (CHK-1), with their lines in the page's language.</summary>
    private async Task<Result<Sale>> ItemsAsync(
        CreateCheckoutSessionRequest request, string locale, CancellationToken cancellationToken)
    {
        if (request.Items is not { Count: > 0 and <= Model.CheckoutSession.MaxItems } requested)
        {
            return Model.CheckoutErrors.InvalidItems("items", $"A payment session sells 1 to {Model.CheckoutSession.MaxItems} items.");
        }

        var currency = request.Currency?.ToUpperInvariant();
        var items = new List<Model.CheckoutItem>();
        var drafts = new List<InvoiceLineDraft>();
        foreach (var (index, item) in requested.Index())
        {
            var field = $"items[{index}]";
            if (await context.FindPriceAsync(item.Price, cancellationToken) is not { } price)
            {
                return Model.CheckoutErrors.InvalidItems($"{field}.price", "No price has this ID or lookup key.");
            }

            if (price.Type != Domain.Catalog.PriceType.OneTime)
            {
                return Model.CheckoutErrors.InvalidItems($"{field}.price", "Payment sessions sell one-time prices; recurring prices are sold by subscriptions.");
            }

            var product = await context.Products.AsNoTracking().SingleAsync(row => row.Id == price.ProductId, cancellationToken);
            if (price.IsArchived || product.IsArchived)
            {
                return Model.CheckoutErrors.InvalidItems($"{field}.price", "The price or its product is archived.");
            }

            currency ??= price.Currency;
            if (price.Currency != currency)
            {
                return Model.CheckoutErrors.InvalidItems($"{field}.price", $"All items are in one currency; this price is in {price.Currency}, not {currency}.");
            }

            var quantity = item.Quantity ?? 1;
            if (quantity is < 1 or > 10_000)
            {
                return Model.CheckoutErrors.InvalidItems($"{field}.quantity", "The quantity is a whole number from 1 to 10,000.");
            }

            items.Add(new Model.CheckoutItem(price.Id, quantity));
            drafts.Add(await lines.FromPriceAsync(price, quantity, null, locale, cancellationToken));
        }

        return new Sale(currency!, items, drafts, null);
    }

    /// <summary>
    /// The recurring prices of a subscription session, which follow the rules of subscriptions (CHK-1, SUB-1), and
    /// one-time prices for its first invoice. Without a trial, the quote is the first invoice; with one, nothing is due
    /// now and one-time prices have no invoice to join. Either way, the quote shows what each renewal bills (CHK-7).
    /// </summary>
    private async Task<Result<Sale>> SubscriptionItemsAsync(CreateCheckoutSessionRequest request, string locale, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (request.Items is not { Count: > 0 } requested)
        {
            return Model.CheckoutErrors.InvalidItems("items", "A subscription session sells at least one recurring price.");
        }

        var specs = new List<SubscriptionItemSpec>();
        var specIndexes = new List<int>();
        var oneTime = new List<(Domain.Catalog.Price Price, int Quantity, string Field)>();
        foreach (var (index, item) in requested.Index())
        {
            var field = $"items[{index}]";
            if (await context.FindPriceAsync(item.Price, cancellationToken) is not { } price)
            {
                return Model.CheckoutErrors.InvalidItems($"{field}.price", "No price has this ID or lookup key.");
            }

            if (price.Type == Domain.Catalog.PriceType.OneTime)
            {
                oneTime.Add((price, item.Quantity ?? 1, field));
                continue;
            }

            var product = await context.Products.AsNoTracking().SingleAsync(row => row.Id == price.ProductId, cancellationToken);
            specs.Add(new SubscriptionItemSpec(price, product, price.UsageType == Domain.Catalog.UsageType.Metered ? item.Quantity : item.Quantity ?? 1));
            specIndexes.Add(index);
        }

        if (specs.Count == 0)
        {
            return Model.CheckoutErrors.InvalidItems("items", "A subscription session sells at least one recurring price.");
        }

        if (SubscriptionItems.Validate(specs, request.Currency?.ToUpperInvariant()) is { } invalid)
        {
            // The rules count the recurring items only; the error names the item as the request has it.
            var field = invalid.Field is { } named && ItemField().Match(named) is { Success: true } match
                ? $"items[{specIndexes[int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture)]}]{match.Groups["rest"].Value}"
                : "items";
            return Model.CheckoutErrors.InvalidItems(field, invalid.Message);
        }

        var data = request.Subscription ?? new CheckoutSubscriptionData();
        if (data.TrialDays is < 0 or > Subscriptions.CreateSubscriptionHandler.MaxTrialDays
            || data.TrialEnd <= now || data.TrialEnd > now.AddDays(Subscriptions.CreateSubscriptionHandler.MaxTrialDays))
        {
            return Model.CheckoutErrors.Invalid(
                data.TrialEnd is null ? "subscription.trialDays" : "subscription.trialEnd", $"A trial ends in the future, at most {Subscriptions.CreateSubscriptionHandler.MaxTrialDays} days away.");
        }

        var zone = billing.Value.Zone;
        var first = specs[0].Price;
        // The prices' default trial is made explicit, so that it is counted from completion like a requested one.
        var trialDays = data.TrialEnd is null ? data.TrialDays ?? specs.Max(spec => (int?)spec.Price.TrialDays) ?? 0 : (int?)null;
        var trial = SubscriptionTrial.Resolve(data.TrialEnd, trialDays, specs.Select(spec => spec.Price), zone, now) is not null;
        var period = BillingPeriods.Period(now, first.Interval!.Value, first.IntervalCount!.Value, 0, zone);
        var recurring = await subscriptionLines.LicensedAsync(specs, period, period, locale, cancellationToken);
        var drafts = new List<InvoiceLineDraft>();
        foreach (var (price, quantity, field) in oneTime)
        {
            if (trial)
            {
                return Model.CheckoutErrors.InvalidItems($"{field}.price", "One-time prices join the first invoice, and a trial has none.");
            }

            if (price.IsArchived || price.Currency != first.Currency || quantity is < 1 or > 10_000)
            {
                return Model.CheckoutErrors.InvalidItems($"{field}.price", $"A one-time price is active, in {first.Currency}, with a quantity from 1 to 10,000.");
            }

            drafts.Add(await lines.FromPriceAsync(price, quantity, null, locale, cancellationToken));
        }

        var items = specs.Select(spec => new Model.CheckoutItem(spec.Price.Id, spec.Quantity))
            .Concat(oneTime.Select(item => new Model.CheckoutItem(item.Price.Id, item.Quantity)))
            .ToList();
        return new Sale(
            first.Currency, items, trial ? [] : [.. recurring, .. drafts], new RecurringSale(first.Interval.Value, first.IntervalCount.Value, recurring, trialDays, data.TrialEnd));
    }

    /// <summary>The subscription to create, as asked; the connection decides how its invoices are collected.</summary>
    private static Model.CheckoutSubscription Subscription(CheckoutSubscriptionData? requested, RecurringSale recurring)
    {
        var data = requested ?? new CheckoutSubscriptionData();
        return new Model.CheckoutSubscription(
            recurring.TrialDays,
            recurring.TrialEnd,
            data.PaymentMethodCollection == PaymentMethodCollectionMode.Always,
            CollectionMethod.ChargeAutomatically,
            data.DaysUntilDue,
            data.Metadata ?? Metadata.Empty);
    }

    /// <summary>A setup session saves a card for a currency and sells nothing.</summary>
    private static Result<Sale> SetupCurrency(CreateCheckoutSessionRequest request)
    {
        if (request.Items is { Count: > 0 })
        {
            return Model.CheckoutErrors.InvalidItems("items", "A setup session sells nothing; leave out the items.");
        }

        var currency = request.Currency?.ToUpperInvariant();
        return currency is not null && Domain.Money.Currency.TryGet(currency, out _)
            ? new Sale(currency, [], [], null)
            : Model.CheckoutErrors.Invalid("currency", "A setup session needs the ISO 4217 currency of the card's future payments.");
    }

    /// <summary>
    /// The currency's routed connection must be able to do what the session needs (CHK-3): save cards for setup and for
    /// a trial that collects one, and take the amount due for payments. A subscription whose connection cannot save
    /// cards is collected by invoices sent to the customer instead; returns the subscription as it will be collected.
    /// </summary>
    private async Task<Result<Model.CheckoutSubscription?>> ConnectionCheckAsync(
        CheckoutMode mode, string currency, Model.CheckoutQuote quote, Model.CheckoutSubscription? subscription, CancellationToken cancellationToken)
    {
        if (await context.RoutedConnectionAsync(currency, cancellationToken) is not { } connection
            || registry.Find(connection.Provider) is not { } provider)
        {
            return Model.CheckoutErrors.CurrencyNotRouted;
        }

        if (connection.CheckoutsPausedAt is not null)
        {
            return Model.CheckoutErrors.Paused;
        }

        // Verification charges (ADP-15) come with the providers that need them; until then, setup needs native support.
        var capabilities = provider.CapabilitiesFor(connection.Settings);
        var setsUp = capabilities.SavesCards != CardSaving.None && capabilities.Setup == CardSetup.Native;
        if (mode == CheckoutMode.Setup || subscription is { CollectsCardForTrial: true, HasTrial: true })
        {
            return setsUp ? subscription : Model.CheckoutErrors.NotSupportedByConnection("save cards without a payment");
        }

        if (subscription is not null && capabilities.SavesCards == CardSaving.None)
        {
            subscription = subscription with { CollectionMethod = CollectionMethod.SendInvoice, DaysUntilDue = subscription.DaysUntilDue ?? 0 };
        }

        var minimum = Math.Max(1, provider.Descriptor.MinimumAmounts.GetValueOrDefault(currency));
        return subscription is { NeedsPayment: false } || quote.AmountDue >= minimum
            ? subscription
            : Model.CheckoutErrors.InvalidItems("items", $"The total due is below the smallest amount the provider takes ({minimum} minor units).");
    }

    [GeneratedRegex(@"^items\[(?<index>\d+)\](?<rest>.*)$")]
    private static partial Regex ItemField();

    /// <summary>What a session sells: its currency and items, the lines due now, and for subscriptions what renewals bill.</summary>
    private sealed record Sale(string Currency, List<Model.CheckoutItem> Items, List<InvoiceLineDraft> Drafts, RecurringSale? Recurring);

    private sealed record RecurringSale(Domain.Catalog.PriceInterval Interval, int IntervalCount, List<InvoiceLineDraft> Lines, int? TrialDays, DateTimeOffset? TrialEnd);
}

public sealed class GetCheckoutSessionHandler(ITahsilDbContext context, IAuthorizer authorizer, CheckoutMapper mapper)
{
    public async Task<Result<CheckoutSessionResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.SubscriptionsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.CheckoutSessions.AsNoTracking().SingleOrDefaultAsync(session => session.Id == id, cancellationToken) is { } session
            ? mapper.ToResponse(session)
            : Model.CheckoutErrors.NotFound;
    }
}

public sealed record ListCheckoutSessionsQuery(CheckoutStatus? Status = null, Guid? Customer = null, string? ClientReferenceId = null, string? Cursor = null, int? Limit = null);

public sealed class ListCheckoutSessionsHandler(ITahsilDbContext context, IAuthorizer authorizer, CheckoutMapper mapper)
{
    private static readonly Keyset<Model.CheckoutSession, Guid> Newest = new("newest", session => session.Id, session => session.Id, Descending: true);

    public async Task<Result<Page<CheckoutSessionResponse>>> HandleAsync(ListCheckoutSessionsQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.SubscriptionsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Paging.IsValidLimit(query.Limit ?? Paging.DefaultLimit))
        {
            return CommonErrors.InvalidLimit();
        }

        var sessions = context.CheckoutSessions.AsNoTracking();
        if (query.Status is { } status)
        {
            var modelStatus = status.Map<Model.CheckoutStatus>();
            sessions = sessions.Where(session => session.Status == modelStatus);
        }

        if (query.Customer is { } customer)
        {
            sessions = sessions.Where(session => session.CustomerId == customer);
        }

        if (query.ClientReferenceId is { } reference)
        {
            sessions = sessions.Where(session => session.ClientReferenceId == reference);
        }

        var page = await Newest.ReadAsync(sessions, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<CheckoutSessionResponse>([.. page.Items.Select(mapper.ToResponse)], page.NextCursor);
    }
}

/// <summary>Expires an open session at the application's request (DOM-10).</summary>
public sealed class ExpireCheckoutSessionHandler(IAuthorizer authorizer, CheckoutExpiry expiry, ITahsilDbContext context, CheckoutMapper mapper)
{
    public async Task<Result<CheckoutSessionResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CheckoutWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await expiry.ExpireAsync(id, cancellationToken) is { IsFailure: true } failed)
        {
            return failed.Error;
        }

        return mapper.ToResponse(await context.CheckoutSessions.AsNoTracking().SingleAsync(session => session.Id == id, cancellationToken));
    }
}

/// <summary>
/// Ends open sessions, when they run out of time or the application asks (DOM-10), with <c>checkout.expired</c>. A
/// payment in flight is reconciled once first (ENG-18): a success found then completes the session instead, and one
/// that arrives later is refunded (PAY-14).
/// </summary>
public sealed class CheckoutExpiry(ITahsilDbContext context, IBillingLocks locks, IAuditLog audit, Payments.PaymentReconciliation reconciliation, BillingClock clock)
{
    public async Task<Result> ExpireAsync(Guid id, CancellationToken cancellationToken)
    {
        var inFlight = await context.Payments.AsNoTracking()
            .Where(payment => payment.CheckoutSessionId == id
                && (payment.Status == Domain.Payments.PaymentStatus.Processing || payment.Status == Domain.Payments.PaymentStatus.RequiresAction))
            .Select(payment => payment.Id)
            .ToListAsync(cancellationToken);
        foreach (var paymentId in inFlight)
        {
            await reconciliation.ReconcileAsync(paymentId, cancellationToken);
        }

        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockCheckoutSessionAsync(id, cancellationToken);
        if (await context.CheckoutSessions.SingleOrDefaultAsync(session => session.Id == id, cancellationToken) is not { } session)
        {
            return Model.CheckoutErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        if (session.Expire(now) is { IsFailure: true } notOpen)
        {
            return notOpen;
        }

        audit.Record(
            AuditActions.CheckoutExpired,
            AuditSubject.Of(AuditSubjects.CheckoutSession, session.Id),
            new Dictionary<string, object?> { ["mode"] = session.Mode, ["clientReferenceId"] = session.ClientReferenceId },
            customerId: session.CustomerId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>Checkout page tokens (CHK-31): <c>cs_</c> and base64url of 32 random bytes, found by their SHA-256.</summary>
public static class CheckoutTokens
{
    public const string Prefix = "cs_";

    public static string New() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SecretHashing.Sha256(token);
}

public sealed class CheckoutMapper(ISecretEncryption encryption, IPublicUrls urls)
{
    public Func<Guid, byte[]> EncryptToken(string token) => id => encryption.Encrypt(token, SecretPurposes.CheckoutToken, id);

    /// <summary>The page's address, while the session is open.</summary>
    public Uri? UrlOf(Model.CheckoutSession session) =>
        session.Status == Model.CheckoutStatus.Open ? urls.Build($"checkout/{encryption.DecryptString(session.Token, SecretPurposes.CheckoutToken, session.Id)}") : null;

    public CheckoutSessionResponse ToResponse(Model.CheckoutSession session) => new()
    {
        Id = session.Id,
        Url = UrlOf(session),
        Mode = session.Mode.Map<CheckoutMode>(),
        Status = session.Status.Map<CheckoutStatus>(),
        CustomerId = session.CustomerId,
        CustomerDetails = session.CustomerDetails is { } details
            ? new CheckoutCustomerDetails
            {
                ExternalId = details.ExternalId,
                Email = details.Email,
                Name = details.Name,
                Type = details.Type.Map<CustomerType>(),
                Phone = details.Phone,
                Address = details.Address is { } address ? new CustomerAddress(address.Line1, address.Line2, address.District, address.City, address.PostalCode, address.Country) : null,
                Locale = details.Locale,
                KimlikSubscriber = details.KimlikSubscriber is { } subscriber ? new KimlikSubscriber(subscriber.Type.Map<KimlikSubscriberType>(), subscriber.Id) : null,
            }
            : null,
        Currency = session.Currency,
        Quote = Quote(session.Quote) with
        {
            Recurring = session.Quote.Recurring is { } recurring
                ? new CheckoutRecurringResponse(recurring.Interval.Map<PriceInterval>(), recurring.IntervalCount, Quote(recurring.Period))
                : null,
        },
        Subscription = session.Subscription is { } subscription
            ? new CheckoutSubscriptionResponse(
                subscription.TrialDays,
                subscription.TrialEnd,
                subscription.CollectsCardForTrial ? PaymentMethodCollectionMode.Always : PaymentMethodCollectionMode.IfRequired,
                subscription.CollectionMethod.Map<Contracts.CollectionMethod>(),
                subscription.DaysUntilDue,
                subscription.Metadata)
            : null,
        AllowPromotionCodes = session.AllowPromotionCodes,
        CouponId = session.CouponId,
        PromotionCodeId = session.PromotionCodeId,
        BillingDetailsCollection = session.BillingDetailsCollection.Map<BillingDetailsCollectionMode>(),
        Locale = session.Locale,
        SuccessUrl = session.SuccessUrl,
        CancelUrl = session.CancelUrl,
        ClientReferenceId = session.ClientReferenceId,
        Metadata = session.Metadata,
        ExpiresAt = session.ExpiresAt,
        CompletedAt = session.CompletedAt,
        ExpiredAt = session.ExpiredAt,
        InvoiceId = session.InvoiceId,
        PaymentId = session.PaymentId,
        PaymentMethodId = session.PaymentMethodId,
        SubscriptionId = session.SubscriptionId,
        CreatedAt = session.CreatedAt,
    };

    private static CheckoutQuoteResponse Quote(Model.CheckoutQuote quote) => new(
        [.. quote.Lines.Select(line => new CheckoutQuoteLineResponse(
            line.PriceId, line.Description, line.Quantity, line.UnitAmountDecimal, line.Amount, line.DiscountAmount, line.TaxPercentage, line.TaxInclusive, line.TaxAmount, line.Total))],
        quote.Subtotal,
        quote.DiscountTotal,
        quote.TaxTotal,
        quote.TotalExcludingTax,
        quote.Total,
        quote.AmountDue);
}
