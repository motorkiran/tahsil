using System.Reflection;
using System.Text.RegularExpressions;

namespace Tahsil.Architecture.Tests;

/// <summary>Rules for what crosses Tahsil's boundaries: the API's contracts and the payment adapters' contract.</summary>
public sealed partial class ContractRuleTests
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    [Fact]
    [Trait("Requirement", "API-3")]
    public void MoneyAmounts_AreIntegersInMinorUnits()
    {
        var amounts = PublicProperties("Tahsil.Contracts").Where(property => Amount().IsMatch(property.Name)).ToList();

        amounts.ShouldNotBeEmpty();
        amounts.Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) != typeof(long))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ShouldBeEmpty();
    }

    [Fact]
    [Trait("Requirement", "ADP-2")]
    [Trait("Requirement", "ADP-14")]
    [Trait("Requirement", "CUS-10")]
    [Trait("Requirement", "SEC-1")]
    public void NeitherTheApiNorTheAdapters_CarryCardDetails()
    {
        PublicProperties("Tahsil.Contracts").Concat(PublicProperties("Tahsil.Providers.Abstractions"))
            .Where(property => CardDetail().IsMatch(property.Name)
                || (property.Name == "Number" && property.DeclaringType!.Name.Contains("Card", StringComparison.Ordinal)))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ShouldBeEmpty();
    }

    [Fact]
    [Trait("Requirement", "ADP-12")]
    public void Adapters_KeepNoStateBetweenCalls()
    {
        var adapters = TahsilAssemblies.Matching(name => name.StartsWith("Tahsil.Providers.", StringComparison.Ordinal))
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.GetInterface("Tahsil.Providers.Abstractions.IPaymentProvider") is not null)
            .ToList();

        adapters.Count.ShouldBeGreaterThanOrEqualTo(5);

        // Captured primary constructor parameters (<name>P) hold the adapter's services.
        adapters.SelectMany(type => type.GetFields(Instance)
                .Where(field => !field.IsInitOnly && !CapturedParameter().IsMatch(field.Name))
                .Select(field => $"{type.Name}.{field.Name}"))
            .ShouldBeEmpty();
    }

    private static IEnumerable<PropertyInfo> PublicProperties(string assembly) =>
        TahsilAssemblies.Named(assembly).GetExportedTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));

    /// <summary>Amounts, totals and balances of money; <c>UnitAmountDecimal</c> is a price per unit below the minor unit.</summary>
    [GeneratedRegex("^(Amount|.*(Amount|Total|Balance|TotalExcludingTax))$")]
    private static partial Regex Amount();

    [GeneratedRegex("^<[A-Za-z]+>P$")]
    private static partial Regex CapturedParameter();

    [GeneratedRegex("^(CardNumber|Pan|Cvc|Cvv2?|SecurityCode|Cavv|Eci|Xid)$", RegexOptions.IgnoreCase)]
    private static partial Regex CardDetail();
}
