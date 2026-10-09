using Tahsil.Domain.Customers;

namespace Tahsil.Domain.Tests.Customers;

public sealed class CustomerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("Requirement", "CUS-4")]
    [InlineData("10000000146", true)]
    [InlineData("12345678950", true)]
    [InlineData("11111111111", false)]
    [InlineData("01234567890", false)]
    [InlineData("12345678951", false)]
    [InlineData("1234567895", false)]
    [InlineData("1234567895a", false)]
    public void Tckns_AreCheckedByTheirDigits(string value, bool valid)
    {
        TaxIds.IsValidTckn(value).ShouldBe(valid);
    }

    [Theory]
    [Trait("Requirement", "CUS-4")]
    [InlineData("10000000146", CustomerType.Individual, null, true)]
    [InlineData("1234567890", CustomerType.Individual, "TR", false)]
    [InlineData("1234567890", CustomerType.Company, "TR", true)]
    [InlineData("10000000146", CustomerType.Company, null, true)]
    [InlineData("123456789", CustomerType.Company, "TR", false)]
    [InlineData("DE123456789", CustomerType.Company, "DE", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456", CustomerType.Individual, "DE", false)]
    public void TaxIds_FollowTheCountryAndType(string taxId, CustomerType type, string? country, bool valid)
    {
        TaxIds.IsValid(taxId, type, country).ShouldBe(valid);
    }

    [Fact]
    [Trait("Requirement", "CUS-8")]
    public void Anonymizing_ClearsExactlyThePersonalData()
    {
        var customer = Customer.Create("user-42", Details() with
        {
            Phone = "0532 123 45 67",
            TaxId = "10000000146",
            TaxOffice = "Kadıköy",
            Address = new CustomerAddress("Moda Cd. 1", null, "Kadıköy", "İstanbul", "34710", "TR"),
            KimlikSubscriber = new KimlikSubscriber(KimlikSubscriberType.User, Guid.CreateVersion7()),
            Metadata = new Dictionary<string, string> { ["plan"] = "pro" },
            IsTaxExempt = true,
        }, Encrypt, Now).Value;

        customer.Anonymize(Now.AddDays(1));

        (customer.ExternalId, customer.Email, customer.Name, customer.Phone, customer.EncryptedTaxId, customer.TaxOffice, customer.Address, customer.KimlikSubscriber)
            .ShouldBe((null, null, null, null, null, null, null, null));
        customer.Metadata.ShouldBeEmpty();
        customer.AnonymizedAt.ShouldBe(Now.AddDays(1));
        (customer.Type, customer.Locale, customer.IsTaxExempt, customer.CreatedAt).ShouldBe((CustomerType.Individual, "tr", true, Now));
        customer.Update(Details(), Encrypt, Now.AddDays(2)).Error.ShouldBe(CustomerErrors.Anonymized);
    }

    [Theory]
    [Trait("Requirement", "CUS-4")]
    [InlineData("0532 123 45 67", null, "+905321234567")]
    [InlineData("(0532) 123-45-67", "TR", "+905321234567")]
    [InlineData("90 532 123 45 67", null, "+905321234567")]
    [InlineData("+49 30 1234567", "DE", "+49301234567")]
    [InlineData("030 1234567", "DE", "030 1234567")]
    [InlineData("ext. 42", null, "ext. 42")]
    public void PhoneNumbers_BecomeE164_WhenTheyReadAsOne(string phone, string? country, string stored)
    {
        var address = country is null ? null : new CustomerAddress(null, null, null, null, null, country);

        Customer.Create(null, Details() with { Phone = phone, Address = address }, Encrypt, Now).Value.Phone.ShouldBe(stored);
    }

    [Theory]
    [Trait("Requirement", "CUS-4")]
    [InlineData("ayse@example.com", true)]
    [InlineData("Ayşe <ayse@example.com>", false)]
    [InlineData("ayse@", false)]
    [InlineData("not an email", false)]
    public void Emails_AreAddresses(string email, bool valid)
    {
        Customer.Create(null, Details() with { Email = email }, Encrypt, Now).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    [Trait("Requirement", "CUS-4")]
    public void TheTaxId_IsKeptOnlyEncrypted_ForItsCustomer()
    {
        Guid? owner = null;
        var customer = Customer.Create(null, Details() with { TaxId = " 10000000146 " }, (id, taxId) =>
        {
            owner = id;
            return [.. System.Text.Encoding.UTF8.GetBytes($"sealed:{taxId}")];
        }, Now).Value;

        owner.ShouldBe(customer.Id);
        System.Text.Encoding.UTF8.GetString(customer.EncryptedTaxId!).ShouldBe("sealed:10000000146");
        Customer.Create(null, Details() with { TaxId = "12345" }, Encrypt, Now).Error.ShouldBe(CustomerErrors.InvalidTaxId);
    }

    private static CustomerDetails Details() => new() { Email = "ayse@example.com", Name = "Ayşe Yılmaz", Locale = "tr" };

    private static byte[] Encrypt(Guid id, string taxId) => [1, 2, 3];
}
