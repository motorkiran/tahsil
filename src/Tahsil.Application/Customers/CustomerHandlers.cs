using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Auditing;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;

using Model = Tahsil.Domain.Customers;

namespace Tahsil.Application.Customers;

public sealed class CreateCustomerHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IIdempotency idempotency, CustomerMapper mapper, BillingClock clock)
{
    public async Task<Result<Versioned<CustomerResponse>>> HandleAsync(CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var created = Model.Customer.Create(request.ExternalId, mapper.FromCreate(request), mapper.EncryptTaxId, clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        var customer = created.Value;
        if (await CustomerConflicts.FindAsync(context, customer, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        context.Customers.Add(customer);
        idempotency.RecordCreated(ResourceTypes.Customer, customer.Id);
        audit.Record(
            AuditActions.CustomerCreated, AuditSubject.Of(AuditSubjects.Customer, customer.Id), new Dictionary<string, object?> { ["externalId"] = customer.ExternalId }, customerId: customer.Id);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            context.DiscardChanges();
            if (await CustomerConflicts.FindAsync(context, customer, cancellationToken) is not { } raced)
            {
                throw;
            }

            return raced;
        }

        return new Versioned<CustomerResponse>(mapper.ToResponse(customer), customer.Version);
    }
}

/// <summary>
/// Creates or updates the customer with an external ID in one call (CUS-2), for applications that sync customers on
/// every sign-in. Safe to repeat and to race: a concurrent create turns into an update.
/// </summary>
public sealed class UpsertCustomerHandler(
    ITahsilDbContext context, IAuthorizer authorizer, CreateCustomerHandler create, UpdateCustomerHandler update)
{
    private const int MaxAttempts = 5;

    public async Task<Result<(Versioned<CustomerResponse> Customer, bool Created)>> HandleAsync(
        string externalId, UpdateCustomerRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        // A concurrent upsert may create the customer first, or update it in between: the merge is then applied again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (await context.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.ExternalId == externalId, cancellationToken) is { } existing)
                {
                    var updated = await update.HandleAsync(existing.Id, request, expectedVersions: null, cancellationToken);
                    return updated.IsFailure ? updated.Error : (updated.Value, false);
                }

                var created = await create.HandleAsync(CustomerMapper.ToCreate(externalId, request), cancellationToken);
                if (created.IsSuccess)
                {
                    return (created.Value, true);
                }

                if (created.Error.Code != "customer.external_id_taken" || attempt == MaxAttempts)
                {
                    return created.Error;
                }

                // The failed create left its audit event behind.
                context.DiscardChanges();
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                context.DiscardChanges();
            }
        }
    }
}

public sealed class UpdateCustomerHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, CustomerMapper mapper, BillingClock clock)
{
    public async Task<Result<Versioned<CustomerResponse>>> HandleAsync(
        Guid id, UpdateCustomerRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.Customers.SingleOrDefaultAsync(customer => customer.Id == id, cancellationToken) is not { } customer)
        {
            return Model.CustomerErrors.NotFound;
        }

        if (Concurrency.Check(customer, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        var before = mapper.ToResponse(customer);
        var updated = customer.Update(mapper.Merge(customer, request), mapper.EncryptTaxId, clock.GetUtcNow());
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        if (await CustomerConflicts.FindAsync(context, customer, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var after = mapper.ToResponse(customer);
        var changed = ChangedFields(before, after);
        if (changed.Count > 0)
        {
            audit.Record(AuditActions.CustomerUpdated, AuditSubject.Of(AuditSubjects.Customer, customer.Id), new Dictionary<string, object?> { ["changed"] = changed }, customerId: customer.Id);
        }

        if (before.IsTaxExempt != after.IsTaxExempt)
        {
            // Drafts follow the customer's tax exemption (INV-4).
            var now = clock.GetUtcNow();
            foreach (var draft in await context.Invoices.Include(invoice => invoice.Lines)
                .Where(invoice => invoice.CustomerId == customer.Id && invoice.Status == Tahsil.Domain.Invoices.InvoiceStatus.Draft).ToListAsync(cancellationToken))
            {
                draft.Recalculate(customer.IsTaxExempt, now);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        return new Versioned<CustomerResponse>(mapper.ToResponse(customer, await context.BalancesOfAsync(customer.Id, cancellationToken)), customer.Version);
    }

    /// <summary>The fields whose values changed, by their API names; the tax ID is compared but never recorded.</summary>
    private static List<string> ChangedFields(CustomerResponse before, CustomerResponse after)
    {
        var fields = new List<string>();
        void Compare(string name, object? left, object? right)
        {
            if (!Equals(left, right))
            {
                fields.Add(name);
            }
        }

        Compare("email", before.Email, after.Email);
        Compare("name", before.Name, after.Name);
        Compare("type", before.Type, after.Type);
        Compare("phone", before.Phone, after.Phone);
        Compare("taxId", before.TaxId, after.TaxId);
        Compare("taxOffice", before.TaxOffice, after.TaxOffice);
        Compare("address", before.Address, after.Address);
        Compare("locale", before.Locale, after.Locale);
        Compare("isTaxExempt", before.IsTaxExempt, after.IsTaxExempt);
        Compare("kimlikSubscriber", before.KimlikSubscriber, after.KimlikSubscriber);
        if (!before.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(after.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
        {
            fields.Add("metadata");
        }

        return fields;
    }
}

public sealed class GetCustomerHandler(ITahsilDbContext context, IAuthorizer authorizer, CustomerMapper mapper)
{
    public async Task<Result<Versioned<CustomerResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken) =>
        await HandleAsync(customer => customer.Id == id, cancellationToken);

    public async Task<Result<Versioned<CustomerResponse>>> HandleByExternalIdAsync(string externalId, CancellationToken cancellationToken) =>
        await HandleAsync(customer => customer.ExternalId == externalId, cancellationToken);

    private async Task<Result<Versioned<CustomerResponse>>> HandleAsync(
        System.Linq.Expressions.Expression<Func<Model.Customer, bool>> predicate, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.Customers.AsNoTracking().SingleOrDefaultAsync(predicate, cancellationToken) is { } customer
            ? new Versioned<CustomerResponse>(mapper.ToResponse(customer, await context.BalancesOfAsync(customer.Id, cancellationToken)), customer.Version)
            : Model.CustomerErrors.NotFound;
    }
}

/// <summary>
/// Lists customers newest first. <c>Email</c> and <c>ExternalId</c> match exactly (the email ignoring case); <c>Search</c>
/// matches the beginning of the email, name or external ID, ignoring case.
/// </summary>
public sealed record ListCustomersQuery(string? Search = null, string? Email = null, string? ExternalId = null, string? Cursor = null, int? Limit = null);

public sealed class ListCustomersHandler(ITahsilDbContext context, IAuthorizer authorizer, CustomerMapper mapper)
{
    private static readonly Keyset<Model.Customer, Guid> Newest = new("newest", customer => customer.Id, customer => customer.Id, Descending: true);

    public async Task<Result<Page<CustomerResponse>>> HandleAsync(ListCustomersQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var limit = query.Limit ?? Paging.DefaultLimit;
        if (!Paging.IsValidLimit(limit))
        {
            return CommonErrors.InvalidLimit();
        }

        var customers = context.Customers.AsNoTracking();

        // Translated to SQL (lower() and LIKE), where neither the culture nor StringComparison applies.
#pragma warning disable CA1304, CA1311, CA1862
        if (Text.Normalize(query.Email) is { } email)
        {
            var lowered = email.ToLowerInvariant();
            customers = customers.Where(customer => customer.Email!.ToLower() == lowered);
        }

        if (Text.Normalize(query.ExternalId) is { } externalId)
        {
            customers = customers.Where(customer => customer.ExternalId == externalId);
        }

        if (Text.Normalize(query.Search) is { } search)
        {
            var term = search.ToLowerInvariant();
            customers = customers.Where(customer =>
                customer.Email!.ToLower().StartsWith(term) || customer.Name!.ToLower().StartsWith(term) || customer.ExternalId!.ToLower().StartsWith(term));
        }
#pragma warning restore CA1304, CA1311, CA1862

        var page = await Newest.ReadAsync(customers, query.Cursor, limit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<CustomerResponse>([.. page.Items.Select(customer => mapper.ToResponse(customer))], page.NextCursor);
    }
}

/// <summary>Tells whether a customer has billing history, by one kind of record (subscriptions, invoices, payments).</summary>
public interface ICustomerHistory
{
    Task<bool> HasHistoryAsync(Guid customerId, CancellationToken cancellationToken);
}

/// <summary>
/// Deletes a customer without billing history (CUS-7), or anonymizes one with it (CUS-8). Subscriptions, invoices
/// and payment methods add their steps when they arrive.
/// </summary>
public sealed class DeleteCustomerHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IEnumerable<ICustomerHistory> histories, CustomerMapper mapper, BillingClock clock)
{
    /// <returns>The anonymized customer, or <see langword="null"/> when the customer was deleted.</returns>
    public async Task<Result<CustomerResponse?>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.Customers.SingleOrDefaultAsync(customer => customer.Id == id, cancellationToken) is not { } customer)
        {
            return Model.CustomerErrors.NotFound;
        }

        var hasHistory = false;
        foreach (var history in histories)
        {
            hasHistory = hasHistory || await history.HasHistoryAsync(customer.Id, cancellationToken);
        }

        if (hasHistory)
        {
            customer.Anonymize(clock.GetUtcNow());
        }
        else
        {
            context.Customers.Remove(customer);
        }

        audit.Record(
            AuditActions.CustomerDeleted,
            AuditSubject.Of(AuditSubjects.Customer, customer.Id),
            new Dictionary<string, object?> { ["anonymized"] = hasHistory },
            customerId: customer.Id);
        audit.Record(AuditActions.DataDestroyed, data: new Dictionary<string, object?>
        {
            ["category"] = hasHistory ? "customer_personal_data" : "customers",
            ["count"] = 1,
        });
        await context.SaveChangesAsync(cancellationToken);
        return hasHistory ? mapper.ToResponse(customer) : null;
    }
}

/// <summary>Everything Tahsil holds about a customer, for data subject requests (SEC-15).</summary>
public sealed class ExportCustomerHandler(
    ITahsilDbContext context, IAuthorizer authorizer, CustomerMapper mapper, ListAuditEventsHandler auditEvents, BillingClock clock)
{
    public async Task<Result<CustomerExportResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == id, cancellationToken) is not { } customer)
        {
            return Model.CustomerErrors.NotFound;
        }

        var trail = new List<AuditEventResponse>();
        string? cursor = null;
        do
        {
            var page = await auditEvents.HandleAsync(new ListAuditEventsQuery(CustomerId: id, Cursor: cursor, Limit: Paging.MaxLimit), cancellationToken, authorize: false);
            trail.AddRange(page.Value.Items);
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        return new CustomerExportResponse(mapper.ToResponse(customer, await context.BalancesOfAsync(customer.Id, cancellationToken)), trail, clock.GetUtcNow());
    }
}

/// <summary>The unique fields of customers: the external ID and the Kimlik link.</summary>
internal static class CustomerConflicts
{
    public static async Task<Error?> FindAsync(ITahsilDbContext context, Model.Customer customer, CancellationToken cancellationToken)
    {
        if (customer.ExternalId is { } externalId
            && await context.Customers.AsNoTracking().Where(other => other.ExternalId == externalId && other.Id != customer.Id)
                .Select(other => (Guid?)other.Id).SingleOrDefaultAsync(cancellationToken) is { } holder)
        {
            return Model.CustomerErrors.ExternalIdTaken(holder);
        }

        if (customer.KimlikSubscriberId is { } subscriberId
            && await context.Customers.AsNoTracking().AnyAsync(
                other => other.KimlikSubscriberType == customer.KimlikSubscriberType && other.KimlikSubscriberId == subscriberId && other.Id != customer.Id,
                cancellationToken))
        {
            return Model.CustomerErrors.KimlikSubscriberTaken;
        }

        return null;
    }
}
