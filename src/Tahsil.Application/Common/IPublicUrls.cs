namespace Tahsil.Application.Common;

/// <summary>Builds absolute URLs from the configured public URL, never from request headers.</summary>
public interface IPublicUrls
{
    /// <summary>
    /// The absolute URL of a path relative to the public URL (which may have a path of its own), with the query
    /// parameters that have a value.
    /// </summary>
    Uri Build(string relativePath, params ReadOnlySpan<(string Name, string? Value)> query);
}
