namespace Tahsil.Admin.Security;

/// <summary>Why a person who signed in at the provider may not use the panel.</summary>
public enum AdminDenial
{
    /// <summary>None of their roles maps to a panel role.</summary>
    NoRole,

    /// <summary>The panel requires a second factor and their sign-in had none.</summary>
    SecondFactorRequired,

    /// <summary>The access token that carries their roles did not validate.</summary>
    InvalidAccessToken,
}

/// <summary>The decision about a sign-in: a panel role, or a reason to deny it.</summary>
public sealed record AdminSignInDecision(string? Role, AdminDenial? Denial)
{
    public static AdminSignInDecision Allow(string role) => new(role, null);

    public static AdminSignInDecision Deny(AdminDenial denial) => new(null, denial);
}

/// <summary>Turns what the provider says about a person into a panel role (KML-2, KML-4).</summary>
public static class AdminSignInRules
{
    public const string MultiFactorMethod = "mfa";

    /// <param name="roleValues">The values of the role claim, from the configured source.</param>
    /// <param name="methods">The <c>amr</c> values of the ID token.</param>
    /// <param name="options">The mappings and the MFA rule.</param>
    public static AdminSignInDecision Decide(IEnumerable<string> roleValues, IEnumerable<string> methods, AdminOidcOptions options)
    {
        var mapped = roleValues.Where(options.RoleMappings.ContainsKey).Select(value => options.RoleMappings[value]);
        if (AdminRoles.MostPrivileged(mapped) is not { } role)
        {
            return AdminSignInDecision.Deny(AdminDenial.NoRole);
        }

        if (options.RequireMfa && !methods.Contains(MultiFactorMethod, StringComparer.Ordinal))
        {
            return AdminSignInDecision.Deny(AdminDenial.SecondFactorRequired);
        }

        return AdminSignInDecision.Allow(role);
    }
}
