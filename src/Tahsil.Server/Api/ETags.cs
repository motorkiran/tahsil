using System.Globalization;
using Microsoft.Net.Http.Headers;
using Tahsil.Application.Common;
using Tahsil.Domain.Common;

namespace Tahsil.Server.Api;

/// <summary>
/// Aggregate versions as strong entity tags: responses carry <c>ETag: "&lt;version&gt;"</c>, and changes accept
/// <c>If-Match</c> with the version the caller last read.
/// </summary>
internal static class ETags
{
    public static string Format(uint version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static void SetETag(this HttpResponse response, AggregateRoot aggregate) =>
        response.Headers.ETag = Format(aggregate.Version);

    /// <summary>
    /// The versions an <c>If-Match</c> header accepts: <see langword="null"/> when there is no header or it is
    /// <c>*</c> (any version), a validation error when it is not a list of Tahsil entity tags.
    /// </summary>
    public static Result<uint[]?> ReadIfMatch(this HttpRequest request)
    {
        var values = request.Headers.IfMatch;
        if (values.Count == 0)
        {
            return (uint[]?)null;
        }

        if (!EntityTagHeaderValue.TryParseStrictList(values, out var tags) || tags.Count == 0)
        {
            return InvalidIfMatch;
        }

        if (tags.Any(tag => tag == EntityTagHeaderValue.Any))
        {
            return (uint[]?)null;
        }

        var versions = new HashSet<uint>();
        foreach (var tag in tags)
        {
            // Weak tags never match: If-Match uses the strong comparison.
            if (tag.IsWeak || !uint.TryParse(tag.Tag.AsSpan()[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                return InvalidIfMatch;
            }

            versions.Add(version);
        }

        return versions.ToArray();
    }

    private static Error InvalidIfMatch =>
        Error.Validation(CommonErrors.InvalidRequestCode, "The If-Match header must be \"*\" or a list of ETags this API returned.");
}
