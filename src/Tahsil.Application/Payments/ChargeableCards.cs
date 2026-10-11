using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Payments;

/// <summary>A saved card with the connection that charges it.</summary>
public sealed record ChargeableCard(PaymentMethod Method, ProviderConnection Connection);

/// <summary>Saved cards that can be charged without the customer (INV-10).</summary>
public sealed class ChargeableCards(ITahsilDbContext context, ProviderRegistry registry)
{
    /// <summary>
    /// An active card of the customer, with consent, whose connection is enabled, fits the mode, takes the currency and
    /// charges saved cards. It need not be the connection routed for the currency: cards stay with the connection that saved them.
    /// </summary>
    public bool CanCharge(PaymentMethod method, ProviderConnection connection, Guid customerId, string currency) =>
        method.IsChargeable
        && method.CustomerId == customerId
        && connection.IsEnabled
        && ProviderRegistry.AllowsInMode(registry.Mode, connection.Provider, connection.Environment)
        && registry.Find(connection.Provider) is { } provider
        && provider.Descriptor.Currencies.Contains(currency)
        && provider.CapabilitiesFor(connection.Settings).ChargesSavedCards;

    /// <summary>The card, if it can be charged for the customer in the currency.</summary>
    public async Task<ChargeableCard?> FindAsync(Guid? methodId, Guid customerId, string currency, CancellationToken cancellationToken)
    {
        if (methodId is not { } id || await context.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken) is not { } method)
        {
            return null;
        }

        var connection = await context.ProviderConnections.AsNoTracking().SingleAsync(row => row.Id == method.ConnectionId, cancellationToken);
        return CanCharge(method, connection, customerId, currency) ? new ChargeableCard(method, connection) : null;
    }

    /// <summary>The card that pays a customer's invoice (INV-10): the subscription's, else the customer's default.</summary>
    public async Task<ChargeableCard?> ForInvoiceAsync(Customer customer, Guid? subscriptionMethodId, string currency, CancellationToken cancellationToken) =>
        await FindAsync(subscriptionMethodId, customer.Id, currency, cancellationToken)
        ?? await FindAsync(customer.DefaultPaymentMethodId, customer.Id, currency, cancellationToken);
}
