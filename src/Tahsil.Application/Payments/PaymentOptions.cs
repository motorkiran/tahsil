namespace Tahsil.Application.Payments;

public sealed class PaymentOptions
{
    public const string SectionName = "Tahsil:Payments";

    /// <summary>How long a customer has to finish a payment on a page before it is abandoned (PAY-8).</summary>
    public TimeSpan CustomerActionTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>When a charge without an answer is first reconciled (ENG-13).</summary>
    public TimeSpan ReconcileAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How old a payment must be before a provider's "not found" counts as "never received" (ENG-15).</summary>
    public TimeSpan NotFoundAfter { get; set; } = TimeSpan.FromMinutes(15);

    internal bool IsValid() => CustomerActionTimeout > TimeSpan.Zero && ReconcileAfter > TimeSpan.Zero && NotFoundAfter > TimeSpan.Zero;
}
