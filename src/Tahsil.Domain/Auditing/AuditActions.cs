namespace Tahsil.Domain.Auditing;

/// <summary>Audit action names, written as <c>resource.past_tense_verb</c>. Features add theirs.</summary>
public static class AuditActions
{
    public const string ApiKeyCreated = "api_key.created";
    public const string ApiKeyRevoked = "api_key.revoked";

    /// <summary>A worker item failed ten times in a row and needs an operator (ENG-6).</summary>
    public const string WorkerAttentionRaised = "worker.attention_raised";
}
