namespace Tahsil.Contracts;

/// <summary>
/// One page of a list. Pass <c>nextCursor</c> as the <c>cursor</c> parameter to get the next page; it is
/// <c>null</c> on the last page. Cursors are opaque.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
