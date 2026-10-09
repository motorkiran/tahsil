namespace Tahsil.Admin.Security;

/// <summary>Admin panel settings from the <c>Tahsil:Admin</c> configuration section.</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Tahsil:Admin";

    /// <summary>Serves the panel on this instance; turn it off on public instances so that only an internal one serves it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long an admin session lasts at most, from sign-in.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Offers "Sign in as developer" with the admin role. Only for Test mode in the Development environment; Tahsil refuses
    /// to start with it anywhere else.
    /// </summary>
    public bool DevelopmentSignIn { get; set; }

    public AdminOidcOptions Oidc { get; set; } = new();

    internal bool IsValid() => SessionLifetime > TimeSpan.Zero && (Oidc.Authority is null || Oidc.IsValid());
}

public enum AdminRoleSource
{
    /// <summary>The role claim of the ID token.</summary>
    IdToken,

    /// <summary>The role claim of the userinfo response.</summary>
    UserInfo,

    /// <summary>The role claim of the access token, validated against the provider's keys (Kimlik puts roles only there).</summary>
    AccessToken,
}

/// <summary>Sign-in through an OpenID Connect provider, such as Kimlik, from <c>Tahsil:Admin:Oidc</c>.</summary>
public sealed class AdminOidcOptions
{
    public Uri? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Space-separated; <c>openid profile email</c> by default.</summary>
    public string Scopes { get; set; } = "openid profile email";

    public string RoleClaim { get; set; } = "roles";

    public AdminRoleSource RoleSource { get; set; } = AdminRoleSource.IdToken;

    /// <summary>The audience access tokens must have when the role comes from them, such as <c>tahsil</c>.</summary>
    public string? AccessTokenAudience { get; set; }

    /// <summary>Provider role values and the panel role each grants, such as <c>tahsil-admin</c> → <c>admin</c>.</summary>
    public Dictionary<string, string> RoleMappings { get; set; } = [];

    /// <summary>Denies sessions whose ID token's <c>amr</c> lacks <c>mfa</c>.</summary>
    public bool RequireMfa { get; set; } = true;

    /// <summary>For Kimlik: the organization whose sign-in rules apply, sent as <c>organization</c>.</summary>
    public string? Organization { get; set; }

    /// <summary>Where people enable a second factor, shown when one is missing; Kimlik's is <c>/account</c>.</summary>
    public Uri? AccountUrl { get; set; }

    internal bool IsValid() =>
        Authority is { IsAbsoluteUri: true }
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(RoleClaim)
        && (RoleSource != AdminRoleSource.AccessToken || !string.IsNullOrWhiteSpace(AccessTokenAudience))
        && RoleMappings.Values.All(role => AdminRoles.All.Contains(role));
}
