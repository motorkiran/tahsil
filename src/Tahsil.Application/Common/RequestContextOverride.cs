namespace Tahsil.Application.Common;

/// <summary>
/// Sets who calls the use cases of a service scope that has no request of its own, such as an admin panel operation
/// running in a Blazor circuit. The request context of the scope is this one when it is set.
/// </summary>
public sealed class RequestContextOverride
{
    public IRequestContext? Context { get; set; }
}
