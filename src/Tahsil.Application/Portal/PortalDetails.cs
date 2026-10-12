using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Application.Customers;
using Tahsil.Contracts;
using Tahsil.Domain.Common;
using Tahsil.Domain.Portal;

namespace Tahsil.Application.Portal;

/// <summary>The billing details a customer keeps up to date in the portal (PRT §2): name, tax ID and office, address and phone.</summary>
public sealed record PortalBillingDetails(string? Name, string? TaxId, string? TaxOffice, string? AddressLine1, string? City, string? PostalCode, string? Phone);

/// <summary>Reads and updates the session's customer's billing details through the API's own use case, as the customer.</summary>
public sealed class PortalDetails(ITahsilDbContext context, IPageCaller caller, CustomerMapper customers, UpdateCustomerHandler update)
{
    public async Task<PortalBillingDetails> ReadAsync(PortalSession session, CancellationToken cancellationToken)
    {
        Use(session);
        var customer = customers.ToResponse(await context.Customers.AsNoTracking().SingleAsync(row => row.Id == session.CustomerId, cancellationToken));
        return new PortalBillingDetails(customer.Name, customer.TaxId, customer.TaxOffice, customer.Address?.Line1, customer.Address?.City, customer.Address?.PostalCode, customer.Phone);
    }

    /// <summary>Validated like any customer's details (CUS-4); the address keeps its other parts.</summary>
    public async Task<Result> UpdateAsync(PortalSession session, PortalBillingDetails details, CancellationToken cancellationToken)
    {
        Use(session);
        var current = customers.ToResponse(await context.Customers.AsNoTracking().SingleAsync(row => row.Id == session.CustomerId, cancellationToken));
        var address = string.IsNullOrWhiteSpace(details.AddressLine1) && string.IsNullOrWhiteSpace(details.City)
            ? null
            : new CustomerAddress(details.AddressLine1, current.Address?.Line2, current.Address?.District, details.City, details.PostalCode, current.Address?.Country ?? "TR");
        var updated = await update.HandleAsync(session.CustomerId, new UpdateCustomerRequest
        {
            Name = details.Name,
            TaxId = details.TaxId,
            TaxOffice = details.TaxOffice,
            Address = address,
            Phone = details.Phone,
        }, null, cancellationToken);
        return updated.IsFailure ? updated.Error : Result.Success();
    }

    private void Use(PortalSession session) => caller.Use(new CustomerCaller(session.CustomerId, CustomerSessionKind.Portal, session.Id));
}
