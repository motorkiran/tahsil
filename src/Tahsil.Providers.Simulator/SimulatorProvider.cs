using System.Net;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Simulator;

/// <summary>The simulator's settings, which restrict its capabilities to mimic a real provider (SIM-2).</summary>
public sealed record SimulatorSettings(CardSetup Setup, RefundSupport Refunds, CardSaving SavesCards)
{
    public static SimulatorSettings Read(IReadOnlyDictionary<string, string> settings) => new(
        settings.GetValueOrDefault("setup") switch
        {
            "verification_charge" => CardSetup.VerificationCharge,
            "none" => CardSetup.None,
            _ => CardSetup.Native,
        },
        settings.GetValueOrDefault("refunds") switch
        {
            "full" => RefundSupport.Full,
            "partial_after_settlement_day" => RefundSupport.PartialAfterSettlementDay,
            _ => RefundSupport.Partial,
        },
        settings.GetValueOrDefault("savesCards") == "during_payment_when_customer_agrees" ? CardSaving.DuringPaymentWhenCustomerAgrees : CardSaving.DuringPayment);
}

/// <summary>
/// The built-in provider of Test mode (SIM-1): it implements the whole contract against <see cref="SimulatorBank"/>,
/// with its card page served by Tahsil at <c>/simulator/pay/{token}</c>.
/// </summary>
/// <param name="bank">The simulated provider.</param>
/// <param name="publicUrl">Tahsil's public URL, under which the card page is served.</param>
public sealed class SimulatorProvider(SimulatorBank bank, Uri publicUrl) : IPaymentProvider
{
    public const string Key = "simulator";

    // Any currency Tahsil charges in (the ISO 4217 table of Tahsil.Domain.Money.Currency).
    private const string CurrencyCodes =
        "AED AFN ALL AMD AOA ARS AUD AWG AZN BAM BBD BDT BHD BIF BMD BND BOB BRL BSD BTN BWP BYN BZD CAD CDF CHF CLP CNY COP CRC CUP CVE "
        + "CZK DJF DKK DOP DZD EGP ERN ETB EUR FJD FKP GBP GEL GHS GIP GMD GNF GTQ GYD HKD HNL HTG HUF IDR ILS INR IQD IRR ISK JMD JOD JPY "
        + "KES KGS KHR KMF KPW KRW KWD KYD KZT LAK LBP LKR LRD LSL LYD MAD MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MYR MZN NAD NGN NIO "
        + "NOK NPR NZD OMR PAB PEN PGK PHP PKR PLN PYG QAR RON RSD RUB RWF SAR SBD SCR SDG SEK SGD SHP SLE SOS SRD SSP STN SVC SYP SZL THB "
        + "TJS TMT TND TOP TRY TTD TWD TZS UAH UGX USD UYU UZS VED VES VND VUV WST XAF XCD XCG XOF XPF YER ZAR ZMW ZWG";

    private static readonly ProviderCapabilities Permissive = new()
    {
        Entry = new HashSet<CardEntry> { CardEntry.Embedded },
        SavesCards = CardSaving.DuringPayment,
        Setup = CardSetup.Native,
        ChargesSavedCards = true,
        Refunds = RefundSupport.Partial,
        Voids = VoidSupport.SameDayFullCancel,
        FindByReference = ReferenceSearch.Any,
        Notifications = NotificationSupport.Verifiable,
        IdempotentCharges = true,
    };

    public ProviderDescriptor Descriptor { get; } = new()
    {
        Key = Key,
        DisplayName = "Simulator",
        Credentials = [],
        Settings =
        [
            new FieldDefinition
            {
                Key = "setup",
                Label = "Saving cards without a payment",
                Type = FieldType.Choice,
                Choices = ["native", "verification_charge", "none"],
                Default = "native",
                Description = "Mimics providers that cannot save a card without charging it.",
            },
            new FieldDefinition
            {
                Key = "refunds",
                Label = "Refunds",
                Type = FieldType.Choice,
                Choices = ["partial", "partial_after_settlement_day", "full"],
                Default = "partial",
                Description = "Mimics providers that refund only in full, or only in full on the day of the payment.",
            },
            new FieldDefinition
            {
                Key = "savesCards",
                Label = "Saving cards during a payment",
                Type = FieldType.Choice,
                Choices = ["during_payment", "during_payment_when_customer_agrees"],
                Default = "during_payment",
                Description = "Mimics providers whose page asks the customer whether to save the card.",
            },
        ],
        Currencies = new HashSet<string>(CurrencyCodes.Split(' '), StringComparer.Ordinal),
        MinimumAmounts = CurrencyCodes.Split(' ').ToDictionary(code => code, _ => 1L, StringComparer.Ordinal),
        Capabilities = Permissive,
        CustomerData = CustomerDataRequirements.None,
        ContentSecurity = new Dictionary<ProviderEnvironment, ContentSecuritySources> { [ProviderEnvironment.Sandbox] = ContentSecuritySources.None },
        Environments = new HashSet<ProviderEnvironment> { ProviderEnvironment.Sandbox },
    };

    public ProviderCapabilities CapabilitiesFor(IReadOnlyDictionary<string, string> settings)
    {
        var simulator = SimulatorSettings.Read(settings);
        return Permissive with { Setup = simulator.Setup, Refunds = simulator.Refunds, SavesCards = simulator.SavesCards };
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ConnectionTestResult.Success);

    public async Task<StartResult> StartAsync(ProviderContext context, StartRequest request, CancellationToken cancellationToken)
    {
        var settings = SimulatorSettings.Read(context.Settings);
        if (request.Purpose == PaymentPurpose.Setup && settings.Setup != CardSetup.Native)
        {
            throw new InvalidOperationException("The connection's settings say it cannot save a card without a payment; check its capabilities first.");
        }

        var (token, expiresAt) = await bank.StartAsync(context.ConnectionId, request, settings, cancellationToken);
        var page = new Uri(publicUrl, $"simulator/pay/{token}");
        return new StartResult
        {
            Presentation = new Presentation.Embed(
                $"""<iframe class="provider-frame" src="{WebUtility.HtmlEncode(page.AbsoluteUri)}" title="Simulator"></iframe>""", []),
            ExpiresAt = expiresAt,
        };
    }

    public Task<PaymentOutcome> CompleteAsync(ProviderContext context, CompleteRequest request, CancellationToken cancellationToken) =>
        bank.CompleteAsync(context.ConnectionId, request.Reference, cancellationToken);

    public Task<PaymentOutcome> ChargeSavedCardAsync(ProviderContext context, ChargeRequest request, CancellationToken cancellationToken) =>
        bank.ChargeAsync(context.ConnectionId, request, cancellationToken);

    public Task<PaymentLookup> FindPaymentAsync(ProviderContext context, PaymentQuery query, CancellationToken cancellationToken) =>
        bank.FindPaymentAsync(context.ConnectionId, query.Reference, cancellationToken);

    public Task<RefundOutcome> RefundAsync(ProviderContext context, RefundRequest request, CancellationToken cancellationToken) =>
        bank.RefundAsync(context.ConnectionId, request, SimulatorSettings.Read(context.Settings), cancellationToken);

    public Task<RefundLookup> FindRefundAsync(ProviderContext context, RefundQuery query, CancellationToken cancellationToken) =>
        bank.FindRefundAsync(context.ConnectionId, query.Reference, cancellationToken);

    public async Task<RemoveCardResult> RemoveSavedCardAsync(ProviderContext context, SavedCardReference card, CancellationToken cancellationToken) =>
        await bank.RemoveCardAsync(context.ConnectionId, card.Token, cancellationToken)
            ? new RemoveCardResult(true)
            : new RemoveCardResult(false, "The simulator has no such saved card.");

    /// <summary>The simulator's signed notifications to itself arrive with provider notifications (SIM-9); until then none verify.</summary>
    public Task<NotificationResult> HandleNotificationAsync(ProviderContext context, IncomingNotification notification, CancellationToken cancellationToken) =>
        Task.FromResult(NotificationResult.Rejected(new NotificationResponse(400, "text/plain", "Unverified")));
}
