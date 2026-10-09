using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Tahsil.Application.Common;

namespace Tahsil.Admin.Security;

/// <summary>
/// How administrators sign in: a cookie session of at most <see cref="AdminOptions.SessionLifetime"/>, started through
/// an OpenID Connect provider (KML-1 to KML-7) or, in Test mode during development, as the developer.
/// </summary>
public static class AdminAuthentication
{
    public const string CookieScheme = "TahsilAdmin";
    public const string OidcScheme = "TahsilAdminOidc";

    /// <summary>Every panel page: an admin session with a panel role.</summary>
    public const string Policy = "TahsilAdmin";

    /// <summary>Pages for the admin role only: API keys, connections, webhooks, provisioning, Test mode tools.</summary>
    public const string AdminOnlyPolicy = "TahsilAdmin.Admin";

    public const string SignInPath = "/admin/signin";
    public const string DeniedPath = "/admin/denied";
    public const string SignOutPath = "/admin/signout";

    private const string DevelopmentSubject = "development";

    internal static void AddAdminAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdminOptions>()
            .BindConfiguration(AdminOptions.SectionName)
            .Validate(
                options => options.IsValid(),
                $"{AdminOptions.SectionName}: the session lifetime must be positive, and an OIDC authority needs a client ID, a role claim, "
                + "an access token audience when roles come from access tokens, and role mappings to admin, operator or viewer.")
            .Validate<IHostEnvironment, IConfiguration>(
                (options, environment, settings) => !options.DevelopmentSignIn || (environment.IsDevelopment() && IsTestMode(settings)),
                $"{AdminOptions.SectionName}:DevelopmentSignIn is only for Test mode in the Development environment.")
            .ValidateOnStart();

        var admin = configuration.GetSection(AdminOptions.SectionName).Get<AdminOptions>() ?? new AdminOptions();
        var authentication = services.AddAuthentication()
            .AddCookie(CookieScheme, cookie =>
            {
                cookie.Cookie.Name = "Tahsil.Admin";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.LoginPath = SignInPath;
                cookie.AccessDeniedPath = DeniedPath;
                cookie.ReturnUrlParameter = "returnUrl";
                cookie.ExpireTimeSpan = admin.SessionLifetime;
                cookie.SlidingExpiration = false;
            });

        if (admin.Oidc.Authority is not null)
        {
            authentication.AddOpenIdConnect(OidcScheme, oidc => ConfigureOidc(oidc, admin.Oidc));
        }

        services.AddAuthorization(AddPolicies);
    }

    /// <summary>The panel's policies: any panel role, and the admin role for admin-only pages.</summary>
    public static void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(CookieScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(AdminClaims.Role, AdminRoles.All));
        options.AddPolicy(AdminOnlyPolicy, policy => policy
            .AddAuthenticationSchemes(CookieScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(AdminClaims.Role, AdminRoles.Admin));
    }

    /// <summary>Starts a sign-in at the provider, signs the developer in, or signs out.</summary>
    internal static void MapAdminAuthentication(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/signin/oidc", (string? returnUrl, IAuthenticationSchemeProvider schemes) =>
            Results.Challenge(new AuthenticationProperties { RedirectUri = LocalOrHome(returnUrl) }, [OidcScheme]))
            .ExcludeFromDescription();

        // Binding the form makes the endpoint check its antiforgery token.
        endpoints.MapPost("/admin/signin/development", async ([FromForm] string? returnUrl, HttpContext context, IOptions<AdminOptions> options) =>
        {
            if (!options.Value.DevelopmentSignIn)
            {
                return Results.NotFound();
            }

            var principal = AdminClaims.Principal(DevelopmentSubject, "developer@tahsil.localhost", "Developer", AdminRoles.Admin, CookieScheme);
            await context.SignInAsync(CookieScheme, principal);
            return Results.LocalRedirect(LocalOrHome(returnUrl));
        }).ExcludeFromDescription();

        endpoints.MapGet(SignOutPath, async (HttpContext context, IAuthenticationSchemeProvider schemes) =>
        {
            await context.SignOutAsync(CookieScheme);
            if (await schemes.GetSchemeAsync(OidcScheme) is not null && context.User.Identity?.AuthenticationType == OidcScheme)
            {
                // RP-initiated logout, with the ID token as the hint.
                return Results.SignOut(new AuthenticationProperties { RedirectUri = SignInPath }, [OidcScheme]);
            }

            return Results.LocalRedirect(SignInPath);
        }).ExcludeFromDescription();
    }

    /// <summary>A path on this site to return to, or the panel's home.</summary>
    internal static string LocalOrHome(string? returnUrl) =>
        returnUrl is { Length: > 1 } && returnUrl[0] == '/' && returnUrl[1] != '/' && returnUrl[1] != '\\' && !returnUrl.Any(char.IsControl)
            ? returnUrl
            : "/admin";

    private static bool IsTestMode(IConfiguration configuration) =>
        configuration.GetSection(ServerOptions.SectionName).GetValue<TahsilMode>(nameof(ServerOptions.Mode)) == TahsilMode.Test;

    private static void ConfigureOidc(OpenIdConnectOptions oidc, AdminOidcOptions settings)
    {
        oidc.SignInScheme = CookieScheme;
        oidc.Authority = settings.Authority!.AbsoluteUri;
        oidc.ClientId = settings.ClientId;
        oidc.ClientSecret = settings.ClientSecret;
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.UsePkce = true;
        oidc.SaveTokens = true;
        oidc.MapInboundClaims = false;
        oidc.CallbackPath = "/signin-oidc";
        oidc.SignedOutCallbackPath = "/signout-callback-oidc";
        oidc.Scope.Clear();
        foreach (var scope in settings.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            oidc.Scope.Add(scope);
        }

        if (settings.RoleSource == AdminRoleSource.UserInfo)
        {
            oidc.GetClaimsFromUserInfoEndpoint = true;
            oidc.ClaimActions.MapJsonKey(settings.RoleClaim, settings.RoleClaim);
        }

        oidc.Events.OnRedirectToIdentityProvider = context =>
        {
            if (settings.Organization is { Length: > 0 } organization)
            {
                context.ProtocolMessage.SetParameter("organization", organization);
            }

            return Task.CompletedTask;
        };

        oidc.Events.OnTicketReceived = async context =>
        {
            var principal = context.Principal!;
            var roleValues = settings.RoleSource == AdminRoleSource.AccessToken
                ? await AccessTokenRolesAsync(context, settings)
                : principal.FindAll(settings.RoleClaim).Select(claim => claim.Value).ToList();

            var decision = roleValues is null
                ? AdminSignInDecision.Deny(AdminDenial.InvalidAccessToken)
                : AdminSignInRules.Decide(roleValues, principal.FindAll("amr").Select(claim => claim.Value), settings);

            if (decision.Role is not { } role)
            {
                context.HandleResponse();
                context.Response.Redirect($"{DeniedPath}?reason={decision.Denial switch
                {
                    AdminDenial.SecondFactorRequired => "mfa",
                    AdminDenial.InvalidAccessToken => "token",
                    _ => "role",
                }}");
                return;
            }

            var session = AdminClaims.Principal(
                principal.FindFirstValue("sub") ?? string.Empty,
                principal.FindFirstValue("email"),
                principal.FindFirstValue("name"),
                role,
                OidcScheme);
            context.Principal = session;
        };
    }

    /// <summary>
    /// The role values of the access token from the token response, once it validates against the provider's keys:
    /// issued by the authority, for <see cref="AdminOidcOptions.AccessTokenAudience"/>, of type <c>at+jwt</c>.
    /// <see langword="null"/> when it does not validate.
    /// </summary>
    private static async Task<List<string>?> AccessTokenRolesAsync(TicketReceivedContext context, AdminOidcOptions settings)
    {
        if (context.Properties?.GetTokenValue("access_token") is not { Length: > 0 } accessToken)
        {
            return null;
        }

        var options = (OpenIdConnectOptions)context.Options;
        var configuration = await options.ConfigurationManager!.GetConfigurationAsync(context.HttpContext.RequestAborted);
        return await AdminAccessTokens.ReadRolesAsync(accessToken, configuration.Issuer, configuration.SigningKeys, settings);
    }
}

/// <summary>Validates access tokens for the panel's roles (KML-3).</summary>
public static class AdminAccessTokens
{
    public static async Task<List<string>?> ReadRolesAsync(string accessToken, string issuer, IEnumerable<SecurityKey> signingKeys, AdminOidcOptions settings)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = settings.AccessTokenAudience,
            IssuerSigningKeys = signingKeys,
            ValidTypes = ["at+jwt"],
            ClockSkew = TimeSpan.FromMinutes(1),
        });

        return result.IsValid ? [.. result.ClaimsIdentity.FindAll(settings.RoleClaim).Select(claim => claim.Value)] : null;
    }
}
