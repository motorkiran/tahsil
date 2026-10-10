using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tahsil.Providers.Abstractions;

/// <summary>
/// Masks secrets and personal data in request and response bodies before they are logged (ADP-9). Fields match by name,
/// ignoring case, at any depth; bodies that are neither JSON nor forms are not logged at all.
/// </summary>
public static class Redaction
{
    public const string Mask = "***";

    /// <summary>Names that are always masked: credentials, tokens and personal data.</summary>
    public static IReadOnlySet<string> DefaultFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "apiKey", "api_key", "secretKey", "secret_key", "secret", "password", "merchant_key", "merchant_salt", "client_secret",
        "authorization", "token", "access_token", "refresh_token", "cardToken", "cardUserKey", "card_token", "utoken", "ctoken",
        "name", "surname", "first_name", "last_name", "contactName", "user_name", "email", "user_email", "phone", "gsmNumber",
        "user_phone", "identityNumber", "identity_number", "tckn", "address", "user_address", "registrationAddress", "ip", "user_ip",
    };

    public static string Body(string? body, string? contentType, IReadOnlySet<string>? fields = null)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        fields ??= DefaultFields;
        if (contentType?.Contains("form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Form(body, fields);
        }

        return Json(body, fields) ?? $"[{body.Length} characters not logged]";
    }

    public static string? Json(string body, IReadOnlySet<string>? fields = null)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        Walk(node, fields ?? DefaultFields);
        return node?.ToJsonString() ?? "null";
    }

    public static string Form(string body, IReadOnlySet<string>? fields = null)
    {
        fields ??= DefaultFields;
        return FormEncoding.Encode(FormEncoding.Decode(body)
            .Select(field => KeyValuePair.Create(field.Key, fields.Contains(field.Key) ? Mask : field.Value)));
    }

    public static IReadOnlySet<string> With(params IEnumerable<string> extra) =>
        new HashSet<string>(DefaultFields.Concat(extra), StringComparer.OrdinalIgnoreCase);

    private static void Walk(JsonNode? node, IReadOnlySet<string> fields)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var (name, child) in value.ToList())
                {
                    if (fields.Contains(name) && child is not (JsonObject or JsonArray))
                    {
                        value[name] = Mask;
                    }
                    else
                    {
                        Walk(child, fields);
                    }
                }

                break;
            case JsonArray items:
                foreach (var item in items)
                {
                    Walk(item, fields);
                }

                break;
        }
    }
}
