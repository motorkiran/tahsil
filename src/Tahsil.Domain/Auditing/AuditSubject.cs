namespace Tahsil.Domain.Auditing;

/// <summary>What an audit event is about.</summary>
/// <param name="Type">One of <see cref="AuditSubjects"/>, or a worker name for worker items.</param>
/// <param name="Id">The record's ID.</param>
public sealed record AuditSubject(string Type, string Id)
{
    public static AuditSubject Of(string type, Guid id) => new(type, id.ToString());
}

/// <summary>Subject types of audit events.</summary>
public static class AuditSubjects
{
    public const string Customer = "customer";
    public const string Product = "product";
    public const string Price = "price";
    public const string TaxRate = "tax_rate";
    public const string Meter = "meter";
    public const string Coupon = "coupon";
    public const string PromotionCode = "promotion_code";
    public const string Connection = "connection";
    public const string PaymentMethod = "payment_method";
    public const string CheckoutSession = "checkout_session";
    public const string Subscription = "subscription";
    public const string Invoice = "invoice";
    public const string InvoiceItem = "invoice_item";
    public const string Payment = "payment";
    public const string Refund = "refund";
    public const string WebhookEndpoint = "webhook_endpoint";
    public const string ApiKey = "api_key";
    public const string TestClock = "test_clock";
}
