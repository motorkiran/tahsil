using System.Buffers.Text;
using System.Security.Cryptography;
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
using Tahsil.Providers.Abstractions;
using Model = Tahsil.Domain.Checkout;
using ModelCustomers = Tahsil.Domain.Customers;

namespace Tahsil.Application.Checkout;

/// <summary>
/// Creates a checkout session (CHK §1): the items and the customer are checked, the currency must be routed to a
/// connection that can do what the session needs, and the quote is computed now; the browser never supplies amounts.
/// </summary>
public sealed class CreateCheckoutSessionHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IIdempotency idempotency,
    InvoiceLines lines,
    ProviderRegistry registry,
    CustomerMapper customers,
    CheckoutMapper mapper,
    IOptions<CheckoutOptions> checkout,
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
        var sold = request.Mode == CheckoutMode.Payment ? await ItemsAsync(request, locale, cancellationToken) : SetupCurrency(request);
        if (sold.IsFailure)
        {
            return sold.Error;
        }

        var (currency, items, drafts) = sold.Value;
        var quote = Model.CheckoutQuote.Of(drafts, customer?.IsTaxExempt ?? false);
        if (await ConnectionCheckAsync(request.Mode, currency, quote, cancellationToken) is { } unsupported)
        {
            return unsupported;
        }

        var now = clock.GetUtcNow();
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
            now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var session = created.Value;
        context.CheckoutSessions.Add(session);
        idempotency.RecordCreated(ResourceTypes.CheckoutSession, session.Id);
        audit.Record(
            AuditActions.CheckoutSessionCreated,
            AuditSubject.Of(AuditSubjects.CheckoutSession, session.Id),
            new Dictionary<string, object?> { ["mode"] = request.Mode, ["currency"] = currency, ["amountDue"] = quote.AmountDue, ["clientReferenceId"] = session.ClientReferenceId },
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

        return (null, new Model.CheckoutCustomer(
            Text.Normalize(given.ExternalId), details.Email, details.Name, details.Type, details.Phone, details.Address, given.Locale, details.KimlikSubscriber));
    }

    /// <summary>The one-time prices sold, in one currency (CHK-1), with their lines in the page's language.</summary>
    private async Task<Result<(string Currency, List<Model.CheckoutItem> Items, List<InvoiceLineDraft> Drafts)>> ItemsAsync(
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

            if (item.Quantity is < 1 or > 10_000)
            {
                return Model.CheckoutErrors.InvalidItems($"{field}.quantity", "The quantity is a whole number from 1 to 10,000.");
            }

            items.Add(new Model.CheckoutItem(price.Id, item.Quantity));
            drafts.Add(await lines.FromPriceAsync(price, item.Quantity, null, locale, cancellationToken));
        }

        return (currency!, items, drafts);
    }

    /// <summary>A setup session saves a card for a currency and sells nothing.</summary>
    private static Result<(string Currency, List<Model.CheckoutItem> Items, List<InvoiceLineDraft> Drafts)> SetupCurrency(CreateCheckoutSessionRequest request)
    {
        if (request.Items is { Count: > 0 })
        {
            return Model.CheckoutErrors.InvalidItems("items", "A setup session sells nothing; leave out the items.");
        }

        var currency = request.Currency?.ToUpperInvariant();
        return currency is not null && Domain.Money.Currency.TryGet(currency, out _)
            ? (currency, [], [])
            : Model.CheckoutErrors.Invalid("currency", "A setup session needs the ISO 4217 currency of the card's future payments.");
    }

    /// <summary>
    /// The currency's routed connection must be able to do what the session needs (CHK-3): save cards for setup, and
    /// take the amount due for payments.
    /// </summary>
    private async Task<Error?> ConnectionCheckAsync(CheckoutMode mode, string currency, Model.CheckoutQuote quote, CancellationToken cancellationToken)
    {
        if (await context.RoutedConnectionAsync(currency, cancellationToken) is not { } connection
            || registry.Find(connection.Provider) is not { } provider)
        {
            return Model.CheckoutErrors.CurrencyNotRouted;
        }

        if (mode == CheckoutMode.Setup)
        {
            return provider.CapabilitiesFor(connection.Settings).SavesCards == CardSaving.None ? Model.CheckoutErrors.NotSupportedByConnection("save cards") : null;
        }

        var minimum = Math.Max(1, provider.Descriptor.MinimumAmounts.GetValueOrDefault(currency));
        return quote.AmountDue < minimum
            ? Model.CheckoutErrors.InvalidItems("items", $"The total due is below the smallest amount the provider takes ({minimum} minor units).")
            : null;
    }
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

/// <summary>Ends open sessions, when they run out of time or the application asks (DOM-10), with <c>checkout.expired</c>.</summary>
public sealed class CheckoutExpiry(ITahsilDbContext context, IBillingLocks locks, IAuditLog audit, BillingClock clock)
{
    public async Task<Result> ExpireAsync(Guid id, CancellationToken cancellationToken)
    {
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
        Quote = new CheckoutQuoteResponse(
            [.. session.Quote.Lines.Select(line => new CheckoutQuoteLineResponse(
                line.PriceId, line.Description, line.Quantity, line.UnitAmountDecimal, line.Amount, line.DiscountAmount, line.TaxPercentage, line.TaxInclusive, line.TaxAmount, line.Total))],
            session.Quote.Subtotal,
            session.Quote.DiscountTotal,
            session.Quote.TaxTotal,
            session.Quote.TotalExcludingTax,
            session.Quote.Total,
            session.Quote.AmountDue),
        AllowPromotionCodes = session.AllowPromotionCodes,
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
        CreatedAt = session.CreatedAt,
    };
}
