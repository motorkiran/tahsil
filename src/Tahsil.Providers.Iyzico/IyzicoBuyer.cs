using System.Globalization;
using System.Text.Json.Nodes;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Iyzico;

/// <summary>The buyer and billing address iyzico requires, from the customer, with the connection's fallbacks (§5).</summary>
internal static class IyzicoBuyer
{
    public const string DefaultIdentityNumber = "11111111111";
    public const string DefaultAddress = "Istanbul, Turkey";
    public const string DefaultCity = "Istanbul";
    public const string DefaultCountry = "Turkey";

    public static JsonObject Buyer(ProviderContext context, CustomerInfo customer)
    {
        var (name, surname) = Split(customer);
        var buyer = new JsonObject
        {
            ["id"] = customer.Id.ToString(),
            ["name"] = name,
            ["surname"] = surname,
            ["identityNumber"] = string.IsNullOrWhiteSpace(customer.TaxId) ? context.Setting("identityNumberFallback") ?? DefaultIdentityNumber : customer.TaxId,
            ["email"] = customer.Email,
            ["registrationAddress"] = Address(context, customer),
            ["city"] = City(context, customer),
            ["country"] = Country(context, customer),
            ["zipCode"] = customer.PostalCode,
            ["ip"] = customer.IpAddress ?? "127.0.0.1",
        };
        if (Phone(customer.Phone) is { } phone)
        {
            buyer["gsmNumber"] = phone;
        }

        return buyer;
    }

    public static JsonObject BillingAddress(ProviderContext context, CustomerInfo customer) => new()
    {
        ["contactName"] = string.IsNullOrWhiteSpace(customer.Name) ? "-" : customer.Name,
        ["address"] = Address(context, customer),
        ["city"] = City(context, customer),
        ["country"] = Country(context, customer),
        ["zipCode"] = customer.PostalCode,
    };

    /// <summary>A person's name split at the last space; a company's whole name with "-" as the surname.</summary>
    private static (string Name, string Surname) Split(CustomerInfo customer)
    {
        var full = string.IsNullOrWhiteSpace(customer.Name) ? "-" : customer.Name.Trim();
        var space = full.LastIndexOf(' ');
        return customer.IsCompany || space <= 0 ? (full, "-") : (full[..space], full[(space + 1)..]);
    }

    private static string Address(ProviderContext context, CustomerInfo customer) =>
        string.IsNullOrWhiteSpace(customer.AddressLine) ? context.Setting("fallbackAddress") ?? DefaultAddress : customer.AddressLine;

    private static string City(ProviderContext context, CustomerInfo customer) =>
        string.IsNullOrWhiteSpace(customer.City) ? context.Setting("fallbackCity") ?? DefaultCity : customer.City;

    /// <summary>
    /// The English name of the customer's country, as iyzico takes it. Turkey is fixed: ICU versions name it differently
    /// (Turkey, Türkiye).
    /// </summary>
    private static string Country(ProviderContext context, CustomerInfo customer)
    {
        if (customer.Country is "TR")
        {
            return DefaultCountry;
        }

        if (customer.Country is { Length: 2 } code)
        {
            try
            {
                return new RegionInfo(code).EnglishName;
            }
            catch (ArgumentException)
            {
                // An unknown code: the fallback below.
            }
        }

        return context.Setting("fallbackCountry") ?? DefaultCountry;
    }

    /// <summary>A Turkish mobile number in iyzico's <c>+90…</c> form; other numbers as they are.</summary>
    private static string? Phone(string? phone) =>
        string.IsNullOrWhiteSpace(phone) ? null : phone.StartsWith('+') ? phone.Replace(" ", string.Empty, StringComparison.Ordinal) : "+90" + phone.TrimStart('0').Replace(" ", string.Empty, StringComparison.Ordinal);
}
