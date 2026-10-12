namespace Tahsil.Application.Kimlik;

/// <summary>The Kimlik connector (KML §2.1): Tahsil's service client in Kimlik and how subscriptions are mirrored.</summary>
public sealed class KimlikOptions
{
    public const string SectionName = "Tahsil:Kimlik";

    public const string KeepUntilFinalAction = "keep_until_final_action";
    public const string EndImmediately = "end_immediately";

    public bool Enabled { get; set; }

    /// <summary>Kimlik's public URL, with a trailing slash.</summary>
    public Uri? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>The <c>whsec_…</c> secret of the endpoint registered in Kimlik for its webhooks (KML-22).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Added to the access end Tahsil writes to Kimlik, so a late renewal does not cut access (KML-14).</summary>
    public TimeSpan SyncBuffer { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Whether a past-due subscription keeps its Kimlik plan until dunning's final action (KML-13).</summary>
    public string PastDueAccess { get; set; } = KeepUntilFinalAction;

    /// <summary>How often every mirrored subscription is synced again (KML-19).</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromHours(6);

    public bool IsValid() =>
        !Enabled
        || (Authority is { IsAbsoluteUri: true } authority && authority.Scheme is "https" or "http" && authority.AbsolutePath.EndsWith('/')
            && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
            && WebhookSecret?.StartsWith("whsec_", StringComparison.Ordinal) == true
            && SyncBuffer >= TimeSpan.Zero && SyncBuffer <= TimeSpan.FromDays(7)
            && PastDueAccess is KeepUntilFinalAction or EndImmediately
            && ReconcileInterval >= TimeSpan.FromMinutes(5));
}
