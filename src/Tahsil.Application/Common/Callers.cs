using Tahsil.Domain.Common;

namespace Tahsil.Application.Common;

/// <summary>Who is calling a use case.</summary>
public abstract record Caller
{
    /// <summary>How the caller is recorded on the records it changes and in the audit trail.</summary>
    public abstract Actor ToActor();
}

/// <summary>An application calling the API with an API key.</summary>
public sealed record ApiKeyCaller(Guid ApiKeyId, IReadOnlySet<string> Permissions) : Caller
{
    public override Actor ToActor() => new(ActorType.ApiKey, ApiKeyId.ToString());
}

/// <summary>An operator signed in to the admin panel through Kimlik or the development sign-in.</summary>
public sealed record AdminCaller(string Subject, string? Email, string Role, IReadOnlySet<string> Permissions) : Caller
{
    public override Actor ToActor() => new(ActorType.Admin, Subject);
}

/// <summary>A customer on a hosted page, holding the session token of a checkout, portal or hosted invoice.</summary>
public sealed record CustomerCaller(Guid CustomerId, CustomerSessionKind Kind, Guid SessionId) : Caller
{
    public override Actor ToActor() => new(ActorType.Customer, CustomerId.ToString());
}

/// <summary>Tahsil itself: workers, outbox handlers and startup provisioning.</summary>
public sealed record SystemCaller : Caller
{
    public static SystemCaller Instance { get; } = new();

    public override Actor ToActor() => Actor.System;
}

public enum CustomerSessionKind
{
    Checkout,
    Portal,
    Invoice,
}
