namespace Tahsil.Domain.Common;

/// <summary>Who did something.</summary>
public enum ActorType
{
    /// <summary>Tahsil itself: a worker, startup provisioning or a provider notification.</summary>
    System,

    /// <summary>An application calling the API with an API key.</summary>
    ApiKey,

    /// <summary>An operator signed in to the admin panel.</summary>
    Admin,

    /// <summary>A customer on a hosted page (checkout, invoice or portal).</summary>
    Customer,
}
