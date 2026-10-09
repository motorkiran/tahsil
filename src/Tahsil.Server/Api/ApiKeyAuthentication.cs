using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using Tahsil.Application.ApiKeys;
using Tahsil.Application.Common;
using Tahsil.Domain.Common;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Api;

/// <summary>Authenticates API requests by their key, <c>Authorization: Bearer ths_…</c>.</summary>
internal sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    private const string BearerPrefix = "Bearer ";
    private const string FailureItemKey = "Tahsil.AuthenticationFailure";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var authenticator = Context.RequestServices.GetRequiredService<ApiKeyAuthenticator>();
        var result = await authenticator.AuthenticateAsync(header[BearerPrefix.Length..].Trim(), Context.RequestAborted);
        if (result.IsFailure)
        {
            Context.Items[FailureItemKey] = result.Error;
            return AuthenticateResult.Fail(result.Error.Message);
        }

        Context.Features.Set<ICallerFeature>(new CallerFeature(result.Value));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, result.Value.ApiKeyId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>Unknown, revoked and expired keys get the same answer; a key of the other mode is told so.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiResults.Problem(Context.Items[FailureItemKey] as Error ?? AuthErrors.Unauthorized).ExecuteAsync(Context);
    }
}

internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>Grants a permission requirement to a caller holding the permission.</summary>
internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var caller = (context.Resource as HttpContext)?.Features.Get<ICallerFeature>()?.Caller;
        var permissions = caller switch
        {
            ApiKeyCaller apiKey => apiKey.Permissions,
            AdminCaller admin => admin.Permissions,
            _ => null,
        };

        if (permissions?.Contains(requirement.Permission) == true)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Answers a missing permission with a problem that names it, like the handlers' own check.</summary>
internal sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<PermissionRequirement>().FirstOrDefault() is { } missing)
        {
            return ApiResults.Problem(AuthErrors.MissingPermission(missing.Permission)).ExecuteAsync(context);
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
