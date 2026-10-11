namespace Tahsil.Domain.Auditing;

/// <summary>Audit action names, written as <c>resource.past_tense_verb</c>. Features add theirs.</summary>
public static class AuditActions
{
    /// <summary>Data was deleted or anonymized under a retention rule; the data says which category and how much (SEC-16).</summary>
    public const string DataDestroyed = "data.destroyed";

    public const string TaxRateCreated = "tax_rate.created";
    public const string TaxRateUpdated = "tax_rate.updated";
    public const string MeterCreated = "meter.created";
    public const string MeterUpdated = "meter.updated";
    public const string ProductCreated = "product.created";
    public const string ProductUpdated = "product.updated";
    public const string ProductDeleted = "product.deleted";
    public const string PriceCreated = "price.created";
    public const string PriceUpdated = "price.updated";

    public const string CustomerCreated = "customer.created";
    public const string CustomerUpdated = "customer.updated";
    public const string CustomerDeleted = "customer.deleted";

    public const string ConnectionCreated = "connection.created";
    public const string ConnectionUpdated = "connection.updated";
    public const string ConnectionDeleted = "connection.deleted";

    /// <summary>The currencies' connections changed; the data has the routing before and after (PAY-6).</summary>
    public const string ConnectionRoutingUpdated = "connection.routing_updated";

    /// <summary>A draft with a draft period was created; the billing engine's cycle invoices (EVT §2).</summary>
    public const string InvoiceCreated = "invoice.created";

    /// <summary>A draft was created by hand, through the API or the admin panel.</summary>
    public const string InvoiceDrafted = "invoice.drafted";
    public const string InvoiceUpdated = "invoice.updated";
    public const string InvoiceDeleted = "invoice.deleted";
    public const string InvoiceFinalized = "invoice.finalized";
    public const string InvoicePaid = "invoice.paid";
    public const string InvoiceVoided = "invoice.voided";
    public const string InvoiceMarkedUncollectible = "invoice.marked_uncollectible";
    public const string InvoiceItemCreated = "invoice_item.created";
    public const string InvoiceItemDeleted = "invoice_item.deleted";
    public const string CustomerBalanceAdjusted = "customer.balance_adjusted";
    public const string PaymentMethodAttached = "payment_method.attached";
    public const string PaymentMethodRemoved = "payment_method.removed";

    /// <summary>A card a subscription uses expires soon (CUS-15).</summary>
    public const string PaymentMethodWillExpire = "payment_method.will_expire";

    public const string InvoicePaymentFailed = "invoice.payment_failed";

    /// <summary>The customer must authenticate the payment on the invoice's hosted page (DUN-4).</summary>
    public const string InvoiceActionRequired = "invoice.action_required";

    /// <summary>An invoice sent for payment passed its due date unpaid (INV-13).</summary>
    public const string InvoiceOverdue = "invoice.overdue";
    public const string PaymentSucceeded = "payment.succeeded";
    public const string PaymentFailed = "payment.failed";
    public const string PaymentCanceled = "payment.canceled";
    public const string CheckoutSessionCreated = "checkout_session.created";
    public const string CheckoutExpired = "checkout.expired";
    public const string CheckoutCompleted = "checkout.completed";

    public const string SubscriptionCreated = "subscription.created";

    /// <summary>Items, the trial, the cancellation schedule, the payment or collection method, or the status changed.</summary>
    public const string SubscriptionUpdated = "subscription.updated";

    /// <summary>A new period started, after a renewal or a trial (ENG-8).</summary>
    public const string SubscriptionRenewed = "subscription.renewed";
    public const string SubscriptionCanceled = "subscription.canceled";

    /// <summary>The trial ends in a few days (SUB-16).</summary>
    public const string SubscriptionTrialWillEnd = "subscription.trial_will_end";

    /// <summary>The subscription renews in a few days (NTF-6).</summary>
    public const string SubscriptionWillRenew = "subscription.will_renew";

    /// <summary>A customer failure made the subscription past due (DUN-5).</summary>
    public const string SubscriptionPastDue = "subscription.past_due";
    public const string RefundCreated = "refund.created";
    public const string RefundSucceeded = "refund.succeeded";
    public const string RefundFailed = "refund.failed";

    /// <summary>Something needs an operator, such as an amount mismatch (PAY-15); the data names the kind.</summary>
    public const string AttentionRaised = "attention.raised";

    public const string AttentionResolved = "attention.resolved";

    /// <summary>An operator settled a payment with the provider's evidence (ENG-16).</summary>
    public const string PaymentResolved = "payment.resolved";

    public const string WebhookEndpointCreated = "webhook_endpoint.created";
    public const string WebhookEndpointUpdated = "webhook_endpoint.updated";
    public const string WebhookEndpointDeleted = "webhook_endpoint.deleted";
    public const string WebhookEndpointSecretRotated = "webhook_endpoint.secret_rotated";

    /// <summary>The billing clock moved forward in Test mode (ENG-22).</summary>
    public const string TestClockAdvanced = "test_clock.advanced";

    public const string ProvisioningApplied = "provisioning.applied";

    public const string ApiKeyCreated = "api_key.created";
    public const string ApiKeyRevoked = "api_key.revoked";

    /// <summary>A worker item failed ten times in a row and needs an operator (ENG-6).</summary>
    public const string WorkerAttentionRaised = "worker.attention_raised";
}
