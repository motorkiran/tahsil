namespace Tahsil.Server.Hosting;

internal static class LocalUrls
{
    /// <summary>
    /// A path on this site: rooted, and not a protocol-relative or backslash URL that browsers treat as another host,
    /// nor one with control characters, which browsers drop (<c>/\t/example.com</c> becomes <c>//example.com</c>).
    /// </summary>
    public static bool IsLocal(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) && !url.Any(char.IsControl);
}
