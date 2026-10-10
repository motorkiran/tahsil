namespace Tahsil.Domain.Invoices;

public enum InvoiceStatus
{
    Draft,
    Open,
    Paid,
    Void,
    Uncollectible,
}

public enum BillingReason
{
    SubscriptionCreate,
    SubscriptionCycle,
    SubscriptionUpdate,
    SubscriptionCancel,
    Checkout,
    Manual,
}

public enum CollectionMethod
{
    ChargeAutomatically,
    SendInvoice,
}

public enum InvoiceLineType
{
    Subscription,
    Usage,
    Proration,
    OneTime,
    Item,
}
