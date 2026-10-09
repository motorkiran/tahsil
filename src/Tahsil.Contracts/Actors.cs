namespace Tahsil.Contracts;

/// <summary>Who did something: an API key, an administrator, a customer on a hosted page, or Tahsil itself.</summary>
/// <param name="Type">The kind of actor.</param>
/// <param name="Id">The API key, administrator subject or customer ID; <see langword="null"/> for the system.</param>
public sealed record ActorResponse(ActorKind Type, string? Id);

public enum ActorKind
{
    System,
    ApiKey,
    Admin,
    Customer,
}
