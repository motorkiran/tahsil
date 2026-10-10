using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tahsil.Domain.Common;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Application.Connections;

/// <summary>Validates credentials and settings against the fields of a provider's descriptor (PAY-1).</summary>
internal static class ConnectionFields
{
    private const int MaxLength = 4000;

    /// <summary>The engine's own setting for providers with direct forms (ADP-13).</summary>
    public static readonly FieldDefinition AllowDirectForm = new()
    {
        Key = "allowDirectForm",
        Label = "Card fields on Tahsil's page",
        Type = FieldType.Boolean,
        Required = false,
        Default = "false",
        Description = "Card fields post straight to the provider from Tahsil's page. This puts the merchant under PCI DSS SAQ A-EP.",
    };

    public static IReadOnlyList<FieldDefinition> SettingsOf(ProviderDescriptor descriptor) =>
        descriptor.Capabilities.Entry.Contains(CardEntry.DirectForm) ? [.. descriptor.Settings, AllowDirectForm] : descriptor.Settings;

    /// <summary>Applies changes to the current credentials: given fields replace, empty ones remove, the rest stay.</summary>
    public static Result<IReadOnlyDictionary<string, string>> Credentials(
        ProviderDescriptor descriptor, IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string>? changes)
    {
        var values = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var (key, value) in changes ?? new Dictionary<string, string>())
        {
            if (!descriptor.Credentials.Any(field => field.Key == key))
            {
                return ConnectionErrors.InvalidCredential(key, "The provider has no such credential.");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value.Trim();
            }
        }

        return Validate(descriptor.Credentials, values, ConnectionErrors.InvalidCredential);
    }

    /// <summary>Applies changes to the current settings: given fields replace, <c>null</c> removes, the rest stay.</summary>
    public static Result<IReadOnlyDictionary<string, string>> Settings(
        ProviderDescriptor descriptor, IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, JsonElement>? changes)
    {
        var fields = SettingsOf(descriptor);
        var values = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var (key, value) in changes ?? new Dictionary<string, JsonElement>())
        {
            if (!fields.Any(field => field.Key == key))
            {
                return ConnectionErrors.InvalidSetting(key, "The provider has no such setting.");
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                    values.Remove(key);
                    break;
                case JsonValueKind.String when string.IsNullOrWhiteSpace(value.GetString()):
                    values.Remove(key);
                    break;
                case JsonValueKind.String:
                    values[key] = value.GetString()!.Trim();
                    break;
                case JsonValueKind.Number:
                    values[key] = value.GetRawText();
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    values[key] = value.GetBoolean() ? "true" : "false";
                    break;
                default:
                    return ConnectionErrors.InvalidSetting(key, "A setting is a string, a number or a boolean.");
            }
        }

        return Validate(fields, values, ConnectionErrors.InvalidSetting);
    }

    /// <summary>A setting's value as JSON: booleans and numbers as such, everything else as a string.</summary>
    public static JsonElement ToJson(FieldDefinition? field, string value) => field?.Type switch
    {
        FieldType.Boolean when bool.TryParse(value, out var flag) => JsonSerializer.SerializeToElement(flag),
        FieldType.Number when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => JsonSerializer.SerializeToElement(number),
        _ => JsonSerializer.SerializeToElement(value),
    };

    private static Result<IReadOnlyDictionary<string, string>> Validate(
        IReadOnlyList<FieldDefinition> fields, Dictionary<string, string> values, Func<string, string, Error> error)
    {
        foreach (var field in fields)
        {
            if (Check(field, values.GetValueOrDefault(field.Key)) is { } message)
            {
                return error(field.Key, message);
            }
        }

        // Fields a newer adapter version no longer has are dropped.
        return values.Where(value => fields.Any(field => field.Key == value.Key)).ToDictionary(StringComparer.Ordinal);
    }

    private static string? Check(FieldDefinition field, string? value)
    {
        if (value is null)
        {
            return field.Required && field.Default is null ? $"'{field.Label}' is required." : null;
        }

        if (value.Length > MaxLength)
        {
            return $"'{field.Label}' is at most {MaxLength} characters.";
        }

        var valid = field.Type switch
        {
            FieldType.Number => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            FieldType.Boolean => value is "true" or "false",
            FieldType.Choice => field.Choices?.Contains(value, StringComparer.Ordinal) == true,
            FieldType.Url => Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps,
            _ => true,
        };
        if (!valid)
        {
            return field.Type switch
            {
                FieldType.Number => $"'{field.Label}' is a number.",
                FieldType.Boolean => $"'{field.Label}' is true or false.",
                FieldType.Choice => $"'{field.Label}' is one of: {string.Join(", ", field.Choices ?? [])}.",
                _ => $"'{field.Label}' is an absolute HTTPS URL.",
            };
        }

        return field.Pattern is { } pattern && !Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1))
            ? $"'{field.Label}' does not have the expected format."
            : null;
    }
}
