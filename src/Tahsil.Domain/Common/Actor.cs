namespace Tahsil.Domain.Common;

/// <summary>Who did something, as recorded on the records it changed.</summary>
/// <param name="Type">The kind of actor.</param>
/// <param name="Id">The API key, admin or customer ID; <see langword="null"/> for the system.</param>
public sealed record Actor(ActorType Type, string? Id)
{
    public const int IdMaxLength = 128;

    public static Actor System { get; } = new(ActorType.System, null);
}
