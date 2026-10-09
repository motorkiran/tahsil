namespace Tahsil.Contracts;

/// <summary>The permissions of the Tahsil API, which API keys and admin roles hold.</summary>
public static class TahsilPermissions
{
    public const string CatalogRead = "tahsil.catalog:read";
    public const string CatalogWrite = "tahsil.catalog:write";
    public const string CustomersRead = "tahsil.customers:read";
    public const string CustomersWrite = "tahsil.customers:write";
    public const string CheckoutWrite = "tahsil.checkout:write";
    public const string SubscriptionsRead = "tahsil.subscriptions:read";
    public const string SubscriptionsWrite = "tahsil.subscriptions:write";
    public const string InvoicesRead = "tahsil.invoices:read";
    public const string InvoicesWrite = "tahsil.invoices:write";
    public const string PaymentsRead = "tahsil.payments:read";
    public const string RefundsWrite = "tahsil.refunds:write";
    public const string CouponsRead = "tahsil.coupons:read";
    public const string CouponsWrite = "tahsil.coupons:write";
    public const string UsageRead = "tahsil.usage:read";
    public const string UsageWrite = "tahsil.usage:write";
    public const string ConnectionsRead = "tahsil.connections:read";
    public const string ConnectionsWrite = "tahsil.connections:write";
    public const string WebhooksRead = "tahsil.webhooks:read";
    public const string WebhooksWrite = "tahsil.webhooks:write";
    public const string ApiKeysRead = "tahsil.api_keys:read";
    public const string ApiKeysWrite = "tahsil.api_keys:write";
    public const string AuditRead = "tahsil.audit:read";
    public const string ProvisioningRead = "tahsil.provisioning:read";
    public const string ProvisioningWrite = "tahsil.provisioning:write";
    public const string TestWrite = "tahsil.test:write";

    public static IReadOnlyList<string> All { get; } =
    [
        CatalogRead, CatalogWrite, CustomersRead, CustomersWrite, CheckoutWrite, SubscriptionsRead, SubscriptionsWrite,
        InvoicesRead, InvoicesWrite, PaymentsRead, RefundsWrite, CouponsRead, CouponsWrite, UsageRead, UsageWrite,
        ConnectionsRead, ConnectionsWrite, WebhooksRead, WebhooksWrite, ApiKeysRead, ApiKeysWrite, AuditRead,
        ProvisioningRead, ProvisioningWrite, TestWrite,
    ];
}
