using System.Text;
using Microsoft.Extensions.Options;

namespace Tahsil.Application.Common;

internal sealed class PublicUrls(IOptions<ServerOptions> server) : IPublicUrls
{
    public Uri Build(string relativePath, params ReadOnlySpan<(string Name, string? Value)> query)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        if (relativePath[0] == '/' || relativePath.Contains("//", StringComparison.Ordinal) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("The path must be relative to the public URL.", nameof(relativePath));
        }

        var publicUrl = server.Value.PublicUrl!.AbsoluteUri;
        var url = new StringBuilder(publicUrl.EndsWith('/') ? publicUrl : publicUrl + "/").Append(relativePath);

        var separator = '?';
        foreach (var (name, value) in query)
        {
            if (value is null)
            {
                continue;
            }

            url.Append(separator).Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return new Uri(url.ToString());
    }
}
