using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Model = Tahsil.Domain.Portal;

namespace Tahsil.Application.Portal;

/// <summary>Portal page tokens (PRT-1): <c>ps_</c> and base64url of 32 random bytes, found by their SHA-256.</summary>
public static class PortalTokens
{
    public const string Prefix = "ps_";

    public static string New() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SecretHashing.Sha256(token);
}

/// <summary>
/// Creates a portal session for a customer the application has authenticated (PRT §1). The page's address is
/// returned now and never again, since only the token's hash is kept; a session is cheap, so a lost answer is
/// answered by creating another.
/// </summary>
public sealed class CreatePortalSessionHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IPublicUrls urls,
    IOptions<PortalOptions> portal,
    IOptions<ServerOptions> server,
    BillingClock clock)
{
    public async Task<Result<PortalSessionResponse>> HandleAsync(CreatePortalSessionRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CheckoutWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        switch (await context.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == request.Customer, cancellationToken))
        {
            case null:
                return CustomerErrors.NotFound;
            case { IsAnonymized: true }:
                return CustomerErrors.Anonymized;
        }

        if (request.Locale is { } locale && !ServerOptions.SupportedLocales.Contains(locale))
        {
            return Model.PortalErrors.Invalid("locale", $"The locale is one of {string.Join(", ", ServerOptions.SupportedLocales)}.");
        }

        if (string.IsNullOrWhiteSpace(request.ReturnUrl))
        {
            return Model.PortalErrors.InvalidUrl;
        }

        // A subscription that ended has nothing to change or cancel, and another customer's is none of this session's.
        if (request.FlowSubscription is { } subscriptionId
            && await context.Subscriptions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == subscriptionId && row.CustomerId == request.Customer, cancellationToken)
                is not { IsTerminal: false })
        {
            return Model.PortalErrors.SubscriptionNotFound;
        }

        var now = clock.GetUtcNow();
        var token = PortalTokens.New();
        var created = Model.PortalSession.Create(
            PortalTokens.Hash(token),
            request.Customer,
            request.ReturnUrl,
            server.Value.IsTestMode,
            request.Locale,
            request.Flow?.Map<Model.PortalFlow>(),
            request.FlowSubscription,
            portal.Value.SessionLifetime,
            now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var session = created.Value;
        context.PortalSessions.Add(session);
        audit.Record(
            AuditActions.PortalSessionCreated,
            AuditSubject.Of(AuditSubjects.Customer, session.CustomerId),
            new Dictionary<string, object?> { ["portalSession"] = session.Id, ["flow"] = request.Flow, ["expiresAt"] = session.ExpiresAt },
            customerId: session.CustomerId);
        await context.SaveChangesAsync(cancellationToken);
        return new PortalSessionResponse(
            session.Id,
            PortalLinks.UrlOf(urls, token),
            session.CustomerId,
            session.ReturnUrl,
            session.Locale,
            request.Flow,
            session.FlowSubscriptionId,
            session.ExpiresAt,
            session.CreatedAt);
    }
}

/// <summary>
/// Portal links for emails (PRT-4): the customer reaches the portal from the email itself, without the application,
/// through a session that lasts <c>Tahsil:Portal:EmailLinkLifetime</c> and opens on the email's task.
/// </summary>
public sealed class PortalLinks(ITahsilDbContext context, IPublicUrls urls, IOptions<PortalOptions> portal, BillingClock clock)
{
    public static Uri UrlOf(IPublicUrls urls, string token) => urls.Build($"portal/{token}");

    /// <summary>The address of a new session, which is saved with whatever the caller saves next.</summary>
    public Uri Create(Guid customerId, Model.PortalFlow? flow, Guid? subscriptionId = null)
    {
        var token = PortalTokens.New();
        var session = Model.PortalSession.Create(
            PortalTokens.Hash(token), customerId, null, false, null, flow, subscriptionId, portal.Value.EmailLinkLifetime, clock.GetUtcNow()).Value;
        context.PortalSessions.Add(session);
        return UrlOf(urls, token);
    }
}
