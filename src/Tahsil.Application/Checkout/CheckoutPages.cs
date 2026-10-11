using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Customers;
using Tahsil.Application.Invoices;
using Tahsil.Application.Payments;
using Tahsil.Domain.Checkout;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;
using PaymentPurpose = Tahsil.Domain.Payments.PaymentPurpose;

namespace Tahsil.Application.Checkout;

/// <summary>Who acts on a hosted page, once the page knows the customer, for the records it writes.</summary>
public interface IPageCaller
{
    void Use(Caller caller);
}

/// <summary>An open session as its page shows it.</summary>
/// <param name="Session">The session.</param>
/// <param name="Customer">Its customer, when known.</param>
/// <param name="Locale">The page's language.</param>
/// <param name="Documents">The legal documents of the page, with their current configuration.</param>
/// <param name="ContentSecurity">The provider's sources for the page's content security policy (FND-42).</param>
/// <param name="CollectsAddress">Whether the form asks for the address and phone (CHK-10).</param>
public sealed record CheckoutView(
    CheckoutSession Session,
    Customer? Customer,
    string Locale,
    IReadOnlyList<(CheckoutDocument Session, LegalDocumentOptions Options)> Documents,
    ContentSecuritySources ContentSecurity,
    bool CollectsAddress);

/// <summary>What the customer submits on the page.</summary>
public sealed record CheckoutSubmission(
    string? Email,
    string? Name,
    CustomerType Type,
    string? TaxId,
    string? TaxOffice,
    CustomerAddress? Address,
    string? Phone,
    IReadOnlySet<string> AcceptedDocuments,
    bool ImmediateStart,
    bool CardStorage,
    string Locale,
    IPAddress? IpAddress,
    string? UserAgent);

public enum CheckoutReturnState
{
    Complete,
    Pending,
    Failed,
}

/// <summary>How a payment attempt ended for the page, with where to go next.</summary>
public sealed record CheckoutReturn(CheckoutReturnState State, string? RedirectUrl, string? FailureCode);

public static class CheckoutPageErrors
{
    public static readonly Error Required = Error.Validation("checkout.details_required", "Fill in the required details.");

    public static readonly Error ConsentRequired = Error.Validation("checkout.consent_required", "Tick the required boxes to continue.");

    public static readonly Error CardNotSaved = Error.Validation("checkout.card_not_saved", "The card was not saved; try again.");
}

/// <summary>
/// The checkout page's use cases (CHK §2): reading an open session by its token, submitting the form (customer,
/// documents and consents, then PAY-8), and the provider's return.
/// </summary>
public sealed class CheckoutPages(
    ITahsilDbContext context,
    IBillingLocks locks,
    IPageCaller caller,
    CustomerMapper customers,
    CustomerPayments payments,
    ProviderRegistry registry,
    ILegalDocumentRenderer renderer,
    LegalDocumentModels models,
    IAuditLog audit,
    CheckoutProtection protection,
    IPublicUrls urls,
    IOptions<CheckoutOptions> options,
    IOptions<CheckoutPageOptions> pageOptions,
    IOptions<MerchantOptions> merchant,
    IOptions<PaymentOptions> paymentOptions,
    IOptions<ServerOptions> server,
    BillingClock clock)
{
    /// <summary>A completed session's page answers for this long, then it is gone (CHK-31).</summary>
    public static readonly TimeSpan CompletedPageLifetime = TimeSpan.FromHours(1);

    /// <summary>The session of a page token: open and in time, or completed within the hour; otherwise none (CHK-31).</summary>
    public async Task<CheckoutSession?> FindAsync(string token, CancellationToken cancellationToken)
    {
        var hash = CheckoutTokens.Hash(token);
        var session = await context.CheckoutSessions.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellationToken);
        var now = clock.GetUtcNow();
        return session switch
        {
            { Status: CheckoutStatus.Open } when session.ExpiresAt > now => session,
            { Status: CheckoutStatus.Complete, CompletedAt: { } completedAt } when now - completedAt < CompletedPageLifetime => session,
            _ => null,
        };
    }

    /// <summary>The page's language: the session's, its customer's, or the installation's default.</summary>
    public async Task<string> LocaleOfAsync(CheckoutSession session, CancellationToken cancellationToken) =>
        session.Locale
        ?? (session.CustomerId is { } customerId ? await context.Customers.AsNoTracking().Where(row => row.Id == customerId).Select(row => row.Locale).SingleAsync(cancellationToken) : null)
        ?? session.CustomerDetails?.Locale
        ?? server.Value.DefaultLocale;

    /// <param name="session">An open or just completed session.</param>
    /// <param name="locale">The page's language, as the request's culture has it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<CheckoutView?> ViewAsync(CheckoutSession session, string locale, CancellationToken cancellationToken)
    {
        var customer = session.CustomerId is { } customerId ? await context.Customers.AsNoTracking().SingleAsync(row => row.Id == customerId, cancellationToken) : null;
        if (await context.RoutedConnectionAsync(session.Currency, cancellationToken) is not { } connection || registry.Find(connection.Provider) is not { } provider)
        {
            return null;
        }

        var environment = connection.Environment == ConnectionEnvironment.Production ? ProviderEnvironment.Production : ProviderEnvironment.Sandbox;
        var requiredFields = provider.Descriptor.CustomerData.CustomerPresent.Where(field => field.Fallback is null).Select(field => field.Field).ToHashSet();
        return new CheckoutView(
            session,
            customer,
            locale,
            Documents(session),
            provider.Descriptor.ContentSecurity.GetValueOrDefault(environment) ?? ContentSecuritySources.None,
            session.BillingDetailsCollection == BillingDetailsCollectionMode.Required || requiredFields.Contains(CustomerField.Address) || requiredFields.Contains(CustomerField.Phone));
    }

    /// <summary>A legal document of the page, rendered with what is known of the customer.</summary>
    public async Task<string?> RenderDocumentAsync(CheckoutSession session, string key, string locale, LegalDocumentCustomer customer, CancellationToken cancellationToken)
    {
        var document = Documents(session).FirstOrDefault(document => document.Session.Key == key).Options;
        return document?.Template is { Count: > 0 } ? await renderer.RenderAsync(document, locale, models.For(session, locale, customer), cancellationToken) : null;
    }

    /// <summary>
    /// Submits the form (CHK-18): the details and consents are checked, the customer is created or updated (CHK-2), the
    /// accepted documents are recorded with the attempt (CHK-16), and the payment is written ahead and started (PAY-8).
    /// </summary>
    public async Task<Result<StartedPayment>> SubmitAsync(string token, CheckoutSubmission submission, CancellationToken cancellationToken)
    {
        if (await FindAsync(token, cancellationToken) is not { Status: CheckoutStatus.Open } found
            || await ViewAsync(found, submission.Locale, cancellationToken) is not { } view)
        {
            return CheckoutErrors.NotOpen;
        }

        var details = Details(found, view, submission);
        if (details.IsFailure)
        {
            return details.Error;
        }

        var documents = view.Documents.Where(document => document.Session.CustomerTypes.Contains(submission.Type)).ToList();
        if (documents.Any(document => document.Session.RequiresAcceptance && !submission.AcceptedDocuments.Contains(document.Session.Key))
            || (found.Mode == CheckoutMode.Setup && !submission.CardStorage))
        {
            return CheckoutPageErrors.ConsentRequired;
        }

        var written = await WriteAheadAsync(found.Id, details.Value, documents, submission, cancellationToken);
        if (written.IsFailure)
        {
            return written.Error;
        }

        var (payment, connection) = written.Value;
        return await payments.StartWrittenAsync(
            payment,
            connection,
            urls.Build($"checkout/{token}/return/{payment.Id}"),
            urls.Build($"checkout/{token}"),
            submission.IpAddress?.ToString(),
            cancellationToken);
    }

    /// <summary>The provider sent the customer back (CHK-19): the payment is completed, which completes the session when it succeeded.</summary>
    public async Task<CheckoutReturn?> ReturnAsync(string token, Guid paymentId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var hash = CheckoutTokens.Hash(token);
        var session = await context.CheckoutSessions.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellationToken);
        if (session?.CustomerId is not { } customerId
            || await context.Payments.AsNoTracking().AnyAsync(payment => payment.Id == paymentId && payment.CheckoutSessionId == session.Id, cancellationToken) is false)
        {
            return null;
        }

        caller.Use(new CustomerCaller(customerId, CustomerSessionKind.Checkout, session.Id));
        var completed = await payments.CompleteAsync(paymentId, values, cancellationToken);
        return completed.IsFailure ? null : await StateAsync(session.Id, completed.Value, cancellationToken);
    }

    /// <summary>Where the page's polling stands (CHK-19): the session's completion, or the attempt's result.</summary>
    public async Task<CheckoutReturn?> StatusAsync(string token, CancellationToken cancellationToken)
    {
        var hash = CheckoutTokens.Hash(token);
        var session = await context.CheckoutSessions.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var latest = await context.Payments.AsNoTracking().Where(payment => payment.CheckoutSessionId == session.Id)
            .OrderByDescending(payment => payment.CreatedAt).ThenByDescending(payment => payment.Id).FirstOrDefaultAsync(cancellationToken);
        return latest is null ? new CheckoutReturn(CheckoutReturnState.Pending, null, null) : await StateAsync(session.Id, latest, cancellationToken);
    }

    private async Task<CheckoutReturn> StateAsync(Guid sessionId, Payment payment, CancellationToken cancellationToken)
    {
        var session = await context.CheckoutSessions.AsNoTracking().SingleAsync(candidate => candidate.Id == sessionId, cancellationToken);
        if (session.Status == CheckoutStatus.Complete)
        {
            return new CheckoutReturn(CheckoutReturnState.Complete, session.SuccessUrlFor(), null);
        }

        return payment.Status switch
        {
            Domain.Payments.PaymentStatus.Succeeded when session.Mode == CheckoutMode.Setup && payment.PaymentMethodId is null =>
                new CheckoutReturn(CheckoutReturnState.Failed, null, CheckoutPageErrors.CardNotSaved.Code),
            Domain.Payments.PaymentStatus.Failed or Domain.Payments.PaymentStatus.Canceled => new CheckoutReturn(CheckoutReturnState.Failed, null, payment.FailureCode),
            _ => new CheckoutReturn(CheckoutReturnState.Pending, null, null),
        };
    }

    private List<(CheckoutDocument Session, LegalDocumentOptions Options)> Documents(CheckoutSession session) =>
    [
        .. session.Documents
            .Select(document => (Session: document, Options: options.Value.Documents.FirstOrDefault(configured => configured.Key == document.Key)))
            .Where(document => document.Options is not null)
            .Select(document => (document.Session, document.Options!)),
    ];

    /// <summary>The billing details the form gives (CHK-10), validated as customers are (CUS-4).</summary>
    private static Result<CustomerDetails> Details(CheckoutSession session, CheckoutView view, CheckoutSubmission submission)
    {
        var company = submission.Type == CustomerType.Company;
        if (string.IsNullOrWhiteSpace(submission.Email) || string.IsNullOrWhiteSpace(submission.Name)
            || (company && (string.IsNullOrWhiteSpace(submission.TaxId) || string.IsNullOrWhiteSpace(submission.TaxOffice)))
            || (view.CollectsAddress && (submission.Address is null || string.IsNullOrWhiteSpace(submission.Address.Line1) || string.IsNullOrWhiteSpace(submission.Address.City)
                || string.IsNullOrWhiteSpace(submission.Phone))))
        {
            return CheckoutPageErrors.Required;
        }

        var details = new CustomerDetails
        {
            Email = submission.Email,
            Name = submission.Name,
            Type = submission.Type,
            Phone = submission.Phone,
            TaxId = submission.TaxId,
            TaxOffice = company ? submission.TaxOffice : null,
            Address = submission.Address,
            Locale = view.Customer?.Locale ?? session.CustomerDetails?.Locale ?? submission.Locale,
            IsTaxExempt = view.Customer?.IsTaxExempt ?? false,
            KimlikSubscriber = view.Customer?.KimlikSubscriber ?? session.CustomerDetails?.KimlikSubscriber,
            Metadata = view.Customer?.Metadata,
        };
        return Customer.Validate(details) is { } invalid ? invalid : details;
    }

    private async Task<Result<(Payment Payment, ProviderConnection Connection)>> WriteAheadAsync(
        Guid sessionId,
        CustomerDetails details,
        IReadOnlyList<(CheckoutDocument Session, LegalDocumentOptions Options)> documents,
        CheckoutSubmission submission,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockCheckoutSessionAsync(sessionId, cancellationToken);
        var session = await context.CheckoutSessions.SingleAsync(candidate => candidate.Id == sessionId, cancellationToken);
        var now = clock.GetUtcNow();
        if (session.Status != CheckoutStatus.Open || session.ExpiresAt <= now)
        {
            return CheckoutErrors.NotOpen;
        }

        if (await context.RoutedConnectionAsync(session.Currency, cancellationToken) is not { } connection)
        {
            return CheckoutErrors.CurrencyNotRouted;
        }

        if (connection.CheckoutsPausedAt is not null)
        {
            return CheckoutErrors.Paused;
        }

        var customer = await CustomerAsync(session, details, now, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error;
        }

        var network = CheckoutProtection.NetworkOf(submission.IpAddress);
        if (await protection.CheckAttemptAsync(session, customer.Value.Id, network, now, cancellationToken) is { } limited)
        {
            return limited;
        }

        caller.Use(new CustomerCaller(customer.Value.Id, CustomerSessionKind.Checkout, session.Id));
        if (session.AttachCustomer(customer.Value.Id, customer.Value.IsTaxExempt, now) is { IsFailure: true } closed)
        {
            return closed.Error;
        }

        var shown = new LegalDocumentCustomer(details.Name, details.Email, details.Type, details.TaxId, details.TaxOffice, details.Address, details.Phone);
        var evidence = new Dictionary<string, string>
        {
            ["amountDue"] = session.Quote.AmountDue.ToString(CultureInfo.InvariantCulture),
            ["currency"] = session.Currency,
            ["mode"] = session.Mode.ToString().ToLowerInvariant(),
        };
        foreach (var (document, configured) in documents.Where(document => submission.AcceptedDocuments.Contains(document.Session.Key)))
        {
            var html = configured.Template is { Count: > 0 }
                ? await renderer.RenderAsync(configured, submission.Locale, models.For(session, submission.Locale, shown), cancellationToken)
                : $"<a href=\"{LegalDocumentOptions.InLocale(configured.Url, submission.Locale)}\">{LegalDocumentOptions.InLocale(configured.Title, submission.Locale)}</a>";
            context.ConsentRecords.Add(ConsentRecord.ForDocument(
                customer.Value.Id,
                ConsentTypeOf(configured.Type),
                new DocumentShown(configured.Key, configured.Version, html),
                configured.CheckboxTextIn(submission.Locale),
                evidence,
                session.Id,
                submission.IpAddress,
                submission.UserAgent,
                now));
        }

        if (submission.ImmediateStart && pageOptions.Value.ImmediateStartConsent.Enabled && submission.Type == CustomerType.Individual)
        {
            context.ConsentRecords.Add(ConsentRecord.Create(
                customer.Value.Id, ConsentType.ImmediateStart, CheckoutTexts.ImmediateStart(pageOptions.Value, submission.Locale), evidence, submission.IpAddress, submission.UserAgent, now, session.Id));
        }

        ConsentRecord? cardStorage = null;
        if (session.Mode == CheckoutMode.Setup)
        {
            cardStorage = ConsentRecord.Create(
                customer.Value.Id, ConsentType.CardStorage, CheckoutTexts.CardStorage(merchant.Value.DisplayName ?? string.Empty, submission.Locale), evidence, submission.IpAddress, submission.UserAgent, now, session.Id);
            context.ConsentRecords.Add(cardStorage);
        }

        var payment = Payment.ForCustomer(
            customer.Value.Id,
            null,
            session.Id,
            connection.Id,
            session.Mode == CheckoutMode.Setup ? 0 : session.Quote.AmountDue,
            session.Currency,
            session.Mode == CheckoutMode.Setup ? PaymentPurpose.Setup : PaymentPurpose.Charge,
            saveCard: session.Mode == CheckoutMode.Setup,
            cardStorage?.Id,
            now + paymentOptions.Value.CustomerActionTimeout,
            new CustomerCaller(customer.Value.Id, CustomerSessionKind.Checkout, session.Id).ToActor(),
            now,
            network);
        context.Payments.Add(payment);
        session.RecordAttempt(now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (payment, connection);
    }

    /// <summary>The session's customer with the submitted details, or a customer found or created for them (CHK-2).</summary>
    private async Task<Result<Customer>> CustomerAsync(CheckoutSession session, CustomerDetails details, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var externalId = session.CustomerDetails?.ExternalId;
        var subscriber = details.KimlikSubscriber;
        var customer = session.CustomerId is { } customerId
            ? await context.Customers.SingleAsync(row => row.Id == customerId, cancellationToken)
            : await context.Customers.SingleOrDefaultAsync(
                row => (externalId != null && row.ExternalId == externalId)
                    || (subscriber != null && row.KimlikSubscriberType == subscriber.Type && row.KimlikSubscriberId == subscriber.Id),
                cancellationToken);
        if (customer is null)
        {
            var created = Customer.Create(externalId, details, customers.EncryptTaxId, now);
            if (created.IsFailure)
            {
                return created.Error;
            }

            customer = created.Value;
            context.Customers.Add(customer);
            caller.Use(new CustomerCaller(customer.Id, CustomerSessionKind.Checkout, session.Id));
            audit.Record(
                Domain.Auditing.AuditActions.CustomerCreated,
                Domain.Auditing.AuditSubject.Of(Domain.Auditing.AuditSubjects.Customer, customer.Id),
                new Dictionary<string, object?> { ["externalId"] = customer.ExternalId },
                customerId: customer.Id);
            return customer;
        }

        caller.Use(new CustomerCaller(customer.Id, CustomerSessionKind.Checkout, session.Id));
        var before = (customer.Email, customer.Name, customer.Type, customer.Phone, customer.TaxOffice, customer.Address, customers.ToResponse(customer).TaxId);
        if (customer.Update(details with { IsTaxExempt = customer.IsTaxExempt, Locale = customer.Locale }, customers.EncryptTaxId, now) is { IsFailure: true } invalid)
        {
            return invalid.Error;
        }

        var changed = new List<string>();
        AddIf(changed, "email", before.Email != customer.Email);
        AddIf(changed, "name", before.Name != customer.Name);
        AddIf(changed, "type", before.Type != customer.Type);
        AddIf(changed, "phone", before.Phone != customer.Phone);
        AddIf(changed, "taxOffice", before.TaxOffice != customer.TaxOffice);
        AddIf(changed, "address", before.Address != customer.Address);
        AddIf(changed, "taxId", before.TaxId != Text.Normalize(details.TaxId));
        if (changed.Count > 0)
        {
            audit.Record(
                Domain.Auditing.AuditActions.CustomerUpdated,
                Domain.Auditing.AuditSubject.Of(Domain.Auditing.AuditSubjects.Customer, customer.Id),
                new Dictionary<string, object?> { ["changed"] = changed },
                customerId: customer.Id);
        }

        return customer;
    }

    private static void AddIf(List<string> changed, string field, bool condition)
    {
        if (condition)
        {
            changed.Add(field);
        }
    }

    private static ConsentType ConsentTypeOf(string type) => type switch
    {
        "pre_information" => ConsentType.PreInformation,
        "distance_sales_contract" => ConsentType.DistanceSalesContract,
        "subscription_contract" => ConsentType.SubscriptionContract,
        "privacy_notice" => ConsentType.PrivacyNoticeShown,
        _ => ConsentType.Custom,
    };
}
