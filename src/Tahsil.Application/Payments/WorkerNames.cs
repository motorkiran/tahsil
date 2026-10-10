namespace Tahsil.Application.Payments;

/// <summary>Names of the payment workers, for signals (ENG-5).</summary>
public static class WorkerNames
{
    public const string PaymentCollection = "payment-collection";
    public const string Reconciliation = "reconciliation";
    public const string RefundReconciliation = "refund-reconciliation";
    public const string ProviderNotifications = "provider-notifications";
}
