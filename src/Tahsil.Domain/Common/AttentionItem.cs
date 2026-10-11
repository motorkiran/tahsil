namespace Tahsil.Domain.Common;

public enum AttentionKind
{
    /// <summary>A provider charged another amount or currency than the payment's (PAY-15).</summary>
    AmountMismatch,

    /// <summary>A payment marked failed succeeded after all, and nothing needed the money (PAY-14).</summary>
    LateSuccess,

    /// <summary>A provider reported a result that contradicts a final payment (PAY-13).</summary>
    ConflictingOutcome,

    /// <summary>Reconciliation found no answer for seven days (ENG-16).</summary>
    ReconciliationOverdue,

    /// <summary>The provider cannot be searched by reference, so an uncertain payment needs an operator (ENG-17).</summary>
    ReconciliationImpossible,

    /// <summary>A refund failed at the provider; the customer has not got their money back (PAY-19).</summary>
    RefundFailed,

    /// <summary>A verified provider notification could not be applied in ten attempts (PAY-24).</summary>
    NotificationFailed,

    /// <summary>More than half of a connection's checkout payments failed lately, a sign of card testing (CHK-22).</summary>
    DeclineSpike,

    /// <summary>A connection's automatic payments fail for the merchant's or provider's reasons, for a day or with a missing permission (DUN-7).</summary>
    MerchantFailures,
}

/// <summary>Something an operator must look at and resolve, such as an amount mismatch; one open item per kind and subject.</summary>
public sealed class AttentionItem
{
    public const int ResolutionMaxLength = 500;

    private AttentionItem()
    {
    }

    public Guid Id { get; private init; }

    public AttentionKind Kind { get; private init; }

    public string SubjectType { get; private init; } = string.Empty;

    public Guid SubjectId { get; private init; }

    public Guid? CustomerId { get; private init; }

    public IReadOnlyDictionary<string, string> Details { get; private init; } = Metadata.Empty;

    public DateTimeOffset RaisedAt { get; private init; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public string? Resolution { get; private set; }

    public Actor? ResolvedBy { get; private set; }

    public bool IsOpen => ResolvedAt is null;

    public static AttentionItem Raise(AttentionKind kind, string subjectType, Guid subjectId, Guid? customerId, IReadOnlyDictionary<string, string> details, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            Kind = kind,
            SubjectType = subjectType,
            SubjectId = subjectId,
            CustomerId = customerId,
            Details = details,
            RaisedAt = now,
        };

    public void Resolve(string resolution, Actor actor, DateTimeOffset now)
    {
        ResolvedAt = now;
        Resolution = resolution.Length > ResolutionMaxLength ? resolution[..ResolutionMaxLength] : resolution;
        ResolvedBy = actor;
    }
}
