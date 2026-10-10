using System.Text;

namespace Tahsil.Providers.Abstractions;

/// <summary><c>application/x-www-form-urlencoded</c> bodies, keeping the order of the fields.</summary>
public static class FormEncoding
{
    public static string Encode(IEnumerable<KeyValuePair<string, string>> fields)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in fields)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    public static FormUrlEncodedContent Content(IEnumerable<KeyValuePair<string, string>> fields) => new(fields);

    /// <summary>Reads a form or query string; a repeated field keeps its last value.</summary>
    public static IReadOnlyDictionary<string, string> Decode(string? form)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(form))
        {
            return fields;
        }

        foreach (var pair in form.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            fields[Unescape(key)] = Unescape(value);
        }

        return fields;
    }

    public static IReadOnlyDictionary<string, string> Decode(byte[] body) => Decode(Encoding.UTF8.GetString(body));

    private static string Unescape(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
