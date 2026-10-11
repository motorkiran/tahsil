namespace Tahsil.Infrastructure.Persistence;

/// <summary>
/// Keys for PostgreSQL advisory locks that serialize work across Tahsil instances.
/// The high bytes spell "TAHSIL" so the locks are recognizable in <c>pg_locks</c>.
/// </summary>
internal static class AdvisoryLockKeys
{
    public const long DatabasePreparation = 0x544148_53494C_0001;

    public const long OutboxCleanup = 0x544148_53494C_0002;

    public const long AuditRetention = 0x544148_53494C_0003;

    public const long IdempotencyCleanup = 0x544148_53494C_0004;

    public const long ProviderNotificationCleanup = 0x544148_53494C_0005;

    public const long WebhookDeliveryCleanup = 0x544148_53494C_0006;

    public const long TimeTravel = 0x544148_53494C_0007;

    public const long OperatorDigest = 0x544148_53494C_0008;
}
