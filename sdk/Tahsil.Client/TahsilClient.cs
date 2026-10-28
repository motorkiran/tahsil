using Microsoft.Extensions.Options;
using Tahsil.Client.Resources;

namespace Tahsil.Client;

/// <summary>
/// The Tahsil API, one client per resource (SDK-4). Register it with
/// <see cref="TahsilClientServiceCollectionExtensions.AddTahsilClient"/>. Failed calls throw <see cref="TahsilApiException"/>.
/// </summary>
public interface ITahsilClient
{
    CustomersClient Customers { get; }

    ProductsClient Products { get; }

    PricesClient Prices { get; }

    TaxRatesClient TaxRates { get; }

    MetersClient Meters { get; }

    CheckoutSessionsClient CheckoutSessions { get; }

    PortalSessionsClient PortalSessions { get; }

    SubscriptionsClient Subscriptions { get; }

    InvoicesClient Invoices { get; }

    InvoiceItemsClient InvoiceItems { get; }

    PaymentsClient Payments { get; }

    RefundsClient Refunds { get; }

    CouponsClient Coupons { get; }

    PromotionCodesClient PromotionCodes { get; }

    UsageEventsClient UsageEvents { get; }

    ProviderConnectionsClient ProviderConnections { get; }

    WebhooksClient Webhooks { get; }

    ApiKeysClient ApiKeys { get; }

    AuditEventsClient AuditEvents { get; }

    ProvisioningClient Provisioning { get; }

    TestClient Test { get; }
}

/// <inheritdoc cref="ITahsilClient"/>
public sealed class TahsilClient : ITahsilClient
{
    public TahsilClient(HttpClient httpClient, IOptions<TahsilClientOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var http = new TahsilHttp(httpClient, options.Value.MaxRetries);
        Customers = new CustomersClient(http);
        Products = new ProductsClient(http);
        Prices = new PricesClient(http);
        TaxRates = new TaxRatesClient(http);
        Meters = new MetersClient(http);
        CheckoutSessions = new CheckoutSessionsClient(http);
        PortalSessions = new PortalSessionsClient(http);
        Subscriptions = new SubscriptionsClient(http);
        Invoices = new InvoicesClient(http);
        InvoiceItems = new InvoiceItemsClient(http);
        Payments = new PaymentsClient(http);
        Refunds = new RefundsClient(http);
        Coupons = new CouponsClient(http);
        PromotionCodes = new PromotionCodesClient(http);
        UsageEvents = new UsageEventsClient(http);
        ProviderConnections = new ProviderConnectionsClient(http);
        Webhooks = new WebhooksClient(http);
        ApiKeys = new ApiKeysClient(http);
        AuditEvents = new AuditEventsClient(http);
        Provisioning = new ProvisioningClient(http);
        Test = new TestClient(http);
    }

    public CustomersClient Customers { get; }

    public ProductsClient Products { get; }

    public PricesClient Prices { get; }

    public TaxRatesClient TaxRates { get; }

    public MetersClient Meters { get; }

    public CheckoutSessionsClient CheckoutSessions { get; }

    public PortalSessionsClient PortalSessions { get; }

    public SubscriptionsClient Subscriptions { get; }

    public InvoicesClient Invoices { get; }

    public InvoiceItemsClient InvoiceItems { get; }

    public PaymentsClient Payments { get; }

    public RefundsClient Refunds { get; }

    public CouponsClient Coupons { get; }

    public PromotionCodesClient PromotionCodes { get; }

    public UsageEventsClient UsageEvents { get; }

    public ProviderConnectionsClient ProviderConnections { get; }

    public WebhooksClient Webhooks { get; }

    public ApiKeysClient ApiKeys { get; }

    public AuditEventsClient AuditEvents { get; }

    public ProvisioningClient Provisioning { get; }

    public TestClient Test { get; }
}
