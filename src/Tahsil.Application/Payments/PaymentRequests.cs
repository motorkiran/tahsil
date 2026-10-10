using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Money;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Application.Payments;

/// <summary>What adapters are told about customers and amounts.</summary>
public sealed class PaymentRequests(ITahsilDbContext context, ISecretEncryption encryption)
{
    public static Amount AmountOf(long minorUnits, string currency) => new(minorUnits, currency, Currency.Get(currency).Exponent);

    public async Task<CustomerInfo> CustomerAsync(Guid customerId, string? ipAddress, CancellationToken cancellationToken)
    {
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == customerId, cancellationToken);
        return new CustomerInfo
        {
            Id = customer.Id,
            Name = customer.Name,
            Email = customer.Email,
            Phone = customer.Phone,
            IsCompany = customer.Type == CustomerType.Company,
            TaxId = customer.EncryptedTaxId is { } taxId ? encryption.DecryptString(taxId, SecretPurposes.CustomerTaxId, customer.Id) : null,
            AddressLine = customer.Address is { } address ? string.Join(", ", new[] { address.Line1, address.Line2, address.District }.Where(part => part is not null)) : null,
            City = customer.Address?.City,
            PostalCode = customer.Address?.PostalCode,
            Country = customer.Address?.Country,
            Locale = customer.Locale,
            IpAddress = ipAddress,
        };
    }

    /// <summary>The provider's own customer for the customer and connection, if the adapter created one before.</summary>
    public async Task<string?> ProviderCustomerAsync(Guid customerId, Guid connectionId, CancellationToken cancellationToken) =>
        await context.ProviderCustomers.AsNoTracking()
            .Where(row => row.CustomerId == customerId && row.ConnectionId == connectionId)
            .Select(row => row.Reference)
            .SingleOrDefaultAsync(cancellationToken);

    public static async Task<bool> HasPaymentInFlightAsync(ITahsilDbContext context, Guid invoiceId, CancellationToken cancellationToken) =>
        await context.Payments.AnyAsync(
            payment => payment.InvoiceId == invoiceId && (payment.Status == Domain.Payments.PaymentStatus.Processing || payment.Status == Domain.Payments.PaymentStatus.RequiresAction),
            cancellationToken);
}
