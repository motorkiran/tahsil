using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Model = Tahsil.Domain.Customers;

namespace Tahsil.Application.Customers;

/// <summary>Customers as the API shows and takes them; tax IDs are decrypted and encrypted here, never in the domain.</summary>
public sealed class CustomerMapper(ISecretEncryption encryption, IOptions<ServerOptions> server)
{
    public CustomerResponse ToResponse(Model.Customer customer, IReadOnlyList<CustomerBalanceResponse>? balances = null) => new(
        customer.Id,
        customer.ExternalId,
        customer.Email,
        customer.Name,
        customer.Type.Map<CustomerType>(),
        customer.Phone,
        customer.EncryptedTaxId is { } encrypted ? encryption.DecryptString(encrypted, SecretPurposes.CustomerTaxId, customer.Id) : null,
        customer.TaxOffice,
        customer.Address is { } address ? new CustomerAddress(address.Line1, address.Line2, address.District, address.City, address.PostalCode, address.Country) : null,
        customer.Locale,
        customer.IsTaxExempt,
        customer.KimlikSubscriber is { } subscriber ? new KimlikSubscriber(subscriber.Type.Map<KimlikSubscriberType>(), subscriber.Id) : null,
        customer.Metadata,
        customer.AnonymizedAt,
        customer.CreatedAt,
        customer.UpdatedAt,
        balances ?? []);

    public Model.CustomerDetails FromCreate(CreateCustomerRequest request) => new()
    {
        Email = request.Email,
        Name = request.Name,
        Type = request.Type.Map<Model.CustomerType>(),
        Phone = request.Phone,
        TaxId = request.TaxId,
        TaxOffice = request.TaxOffice,
        Address = ToModel(request.Address),
        Locale = request.Locale ?? server.Value.DefaultLocale,
        IsTaxExempt = request.IsTaxExempt,
        KimlikSubscriber = ToModel(request.KimlikSubscriber),
        Metadata = request.Metadata,
    };

    /// <summary>The customer's details with the patch applied: omitted fields stay, <c>null</c> clears.</summary>
    public Model.CustomerDetails Merge(Model.Customer customer, UpdateCustomerRequest patch)
    {
        var current = ToResponse(customer);
        return new Model.CustomerDetails
        {
            Email = patch.HasEmail ? patch.Email : current.Email,
            Name = patch.HasName ? patch.Name : current.Name,
            Type = patch.Type?.Map<Model.CustomerType>() ?? customer.Type,
            Phone = patch.HasPhone ? patch.Phone : current.Phone,
            TaxId = patch.HasTaxId ? patch.TaxId : current.TaxId,
            TaxOffice = patch.HasTaxOffice ? patch.TaxOffice : current.TaxOffice,
            Address = patch.HasAddress ? ToModel(patch.Address) : customer.Address,
            Locale = patch.Locale ?? customer.Locale,
            IsTaxExempt = patch.IsTaxExempt ?? customer.IsTaxExempt,
            KimlikSubscriber = patch.HasKimlikSubscriber ? ToModel(patch.KimlikSubscriber) : customer.KimlikSubscriber,
            Metadata = patch.HasMetadata ? patch.Metadata : customer.Metadata,
        };
    }

    /// <summary>A patch applied to no customer: what an upsert creates.</summary>
    public static CreateCustomerRequest ToCreate(string externalId, UpdateCustomerRequest patch) => new()
    {
        ExternalId = externalId,
        Email = patch.Email,
        Name = patch.Name,
        Type = patch.Type ?? CustomerType.Individual,
        Phone = patch.Phone,
        TaxId = patch.TaxId,
        TaxOffice = patch.TaxOffice,
        Address = patch.Address,
        Locale = patch.Locale,
        IsTaxExempt = patch.IsTaxExempt ?? false,
        KimlikSubscriber = patch.KimlikSubscriber,
        Metadata = patch.Metadata,
    };

    public byte[] EncryptTaxId(Guid customerId, string taxId) => encryption.Encrypt(taxId, SecretPurposes.CustomerTaxId, customerId);

    private static Model.CustomerAddress? ToModel(CustomerAddress? address) =>
        address is null ? null : new Model.CustomerAddress(address.Line1, address.Line2, address.District, address.City, address.PostalCode, address.Country);

    private static Model.KimlikSubscriber? ToModel(KimlikSubscriber? subscriber) =>
        subscriber is null ? null : new Model.KimlikSubscriber(subscriber.Type.Map<Model.KimlikSubscriberType>(), subscriber.Id);
}
