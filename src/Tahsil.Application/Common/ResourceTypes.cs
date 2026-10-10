namespace Tahsil.Application.Common;

/// <summary>Names of the resources that create handlers record for idempotent retries (API-13).</summary>
public static class ResourceTypes
{
    public const string TaxRate = "tax_rate";
    public const string Meter = "meter";
    public const string Product = "product";
    public const string Price = "price";
    public const string Customer = "customer";
    public const string Connection = "provider_connection";
    public const string Invoice = "invoice";
    public const string InvoiceItem = "invoice_item";
    public const string BalanceTransaction = "balance_transaction";
}
