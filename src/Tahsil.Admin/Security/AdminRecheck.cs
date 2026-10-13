using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Kimlik;
using Tahsil.Domain.Common;

namespace Tahsil.Admin.Security;

/// <summary>
/// With the Kimlik connector on, a panel session is rechecked every five minutes through Kimlik's Management API
/// (KML-9): a user that is gone, suspended or no longer holds the role the session has loses it; a changed role is
/// taken at the next sign-in. When Kimlik cannot be reached, the session stays until the next check. When
/// a session was checked is kept in memory, not in its cookie: rewriting the cookie would extend its absolute lifetime.
/// </summary>
public sealed class AdminRecheck(IKimlikClient kimlik, IMemoryCache checks, IOptions<KimlikOptions> connector, IOptions<AdminOptions> admin)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Whether the session is one to recheck: signed in through Kimlik while the connector is on.</summary>
    public bool Applies(ClaimsPrincipal principal) =>
        connector.Value.Enabled && admin.Value.Oidc.Authority is not null && principal.Identity?.AuthenticationType == AdminAuthentication.OidcScheme;

    /// <summary>Whether the session may go on: <see langword="false"/> only when Kimlik says it is over.</summary>
    public async Task<bool> StillValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.FindFirstValue(AdminClaims.Subject), out var subject))
        {
            return false;
        }

        var user = await kimlik.GetUserAsync(subject, cancellationToken);
        if (user.IsFailure)
        {
            return user.Error!.Type != ErrorType.NotFound;
        }

        var mappings = admin.Value.Oidc.RoleMappings;
        return user.Value.Status == KimlikUserStatus.Active
            && AdminRoles.MostPrivileged(user.Value.Roles.Where(mappings.ContainsKey).Select(role => mappings[role])) == principal.FindFirstValue(AdminClaims.Role);
    }

    /// <summary>The cookie's check (KML-9) on requests to the panel.</summary>
    internal static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is { } principal
            && !await context.HttpContext.RequestServices.GetRequiredService<AdminRecheck>().SessionGoesOnAsync(principal, context.Properties.IssuedUtc, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AdminAuthentication.CookieScheme);
        }
    }

    /// <summary>Whether a session that started at <paramref name="issuedAt"/> goes on; asks Kimlik once every five minutes per session and instance.</summary>
    internal async Task<bool> SessionGoesOnAsync(ClaimsPrincipal principal, DateTimeOffset? issuedAt, CancellationToken cancellationToken)
    {
        if (!Applies(principal))
        {
            return true;
        }

        var session = $"admin-recheck:{principal.FindFirstValue(AdminClaims.Subject)}:{issuedAt?.UtcTicks}";
        if (checks.TryGetValue(session, out _))
        {
            return true;
        }

        if (!await StillValidAsync(principal, cancellationToken))
        {
            return false;
        }

        checks.Set(session, true, Interval);
        return true;
    }
}

/// <summary>
/// The panel's circuits check their session every five minutes too (KML-9): a circuit outlives the requests that
/// carry the cookie, so it would otherwise keep a session Kimlik has ended.
/// </summary>
internal sealed class AdminRevalidation(ILoggerFactory logging, IServiceScopeFactory scopes)
    : RevalidatingServerAuthenticationStateProvider(logging)
{
    protected override TimeSpan RevalidationInterval => AdminRecheck.Interval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var recheck = scope.ServiceProvider.GetRequiredService<AdminRecheck>();
        if (!recheck.Applies(authenticationState.User))
        {
            return true;
        }

        return await recheck.StillValidAsync(authenticationState.User, cancellationToken);
    }
}
