using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;
using Model = Tahsil.Domain.Payments;

namespace Tahsil.Application.Connections;

/// <summary>How a connection saves a card without a purchase: natively, with a verification charge (ADP-15), or not at all.</summary>
public static class CardSetups
{
    /// <summary>
    /// What a setup charges through the connection in <paramref name="currency"/>: 0 when the provider saves cards
    /// without a payment, the verification charge (at least the provider's minimum) when the connection has one on,
    /// otherwise <see langword="null"/>: the connection cannot save a card without a purchase.
    /// </summary>
    public static long? AmountFor(ProviderConnection connection, IPaymentProvider provider, string currency)
    {
        var capabilities = provider.CapabilitiesFor(connection.Settings);
        if (capabilities.SavesCards == CardSaving.None)
        {
            return null;
        }

        return capabilities.Setup switch
        {
            CardSetup.Native => 0,
            CardSetup.VerificationCharge when connection.VerificationChargeAmount is { } amount =>
                Math.Max(amount, provider.Descriptor.MinimumAmounts.GetValueOrDefault(currency)),
            _ => null,
        };
    }

    /// <summary>Applies a requested verification charge: only connections whose provider needs one can turn it on.</summary>
    internal static Domain.Common.Result Apply(ProviderConnection connection, Contracts.VerificationCharge requested, IPaymentProvider provider, DateTimeOffset now)
    {
        if (requested.Enabled && provider.CapabilitiesFor(connection.Settings) is not { Setup: CardSetup.VerificationCharge, SavesCards: not CardSaving.None })
        {
            return Model.ConnectionErrors.VerificationChargeNotApplicable;
        }

        return connection.SetVerificationCharge(requested.Enabled ? requested.Amount : null, now);
    }

    internal static Contracts.VerificationCharge? ToResponse(ProviderConnection connection) =>
        connection.VerificationChargeAmount is { } amount ? new Contracts.VerificationCharge(true, amount) : null;
}
