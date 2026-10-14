using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Tahsil.Admin.Settings;

/// <summary>A configuration section as the installation runs with it.</summary>
public sealed record SettingsSection(string Name, IReadOnlyList<Setting> Settings);

/// <summary>One setting; a secret's value is never read out, only whether it is set.</summary>
public sealed record Setting(string Key, string Value, bool IsSecret);

/// <summary>
/// The installation's effective configuration (ADM §3): every options class with a <c>SectionName</c>, as it is bound,
/// defaults included. Settings whose names end like secrets are masked.
/// </summary>
public sealed partial class EffectiveSettings(IServiceProvider services, IConfiguration configuration)
{
    public const string Masked = "••••••";
    public const string NotSet = "–";

    private const int MaxDepth = 5;

    public IReadOnlyList<SettingsSection> Read()
    {
        var sections = new List<SettingsSection>
        {
            new("ConnectionStrings", [new Setting("Tahsil", string.IsNullOrEmpty(configuration.GetConnectionString("Tahsil")) ? NotSet : Masked, IsSecret: true)]),
        };
        var types = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("Tahsil.", StringComparison.Ordinal) == true)
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && SectionOf(type) is not null)
            .OrderBy(type => SectionOf(type), StringComparer.Ordinal)
            .ThenBy(type => type.Name, StringComparer.Ordinal);
        foreach (var group in types.GroupBy(type => SectionOf(type)!))
        {
            var settings = new List<Setting>();
            foreach (var type in group)
            {
                if (services.GetService(typeof(IOptions<>).MakeGenericType(type)) is IOptions<object> options)
                {
                    Describe(options.Value, prefix: string.Empty, secret: false, depth: 0, settings);
                }
            }

            if (settings.Count > 0)
            {
                sections.Add(new SettingsSection(group.Key, [.. settings.DistinctBy(setting => setting.Key).OrderBy(setting => setting.Key, StringComparer.Ordinal)]));
            }
        }

        return sections;
    }

    private static string? SectionOf(Type type) =>
        type.GetField("SectionName", BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true, FieldType: var fieldType } field && fieldType == typeof(string)
            ? (string?)field.GetRawConstantValue()
            : null;

    private static void Describe(object? value, string prefix, bool secret, int depth, List<Setting> settings)
    {
        switch (value)
        {
            case null:
                settings.Add(new Setting(prefix, NotSet, secret));
                return;
            case string text:
                settings.Add(new Setting(prefix, text.Length == 0 ? NotSet : secret ? Masked : text, secret));
                return;
            case IFormattable formattable when IsSimple(value.GetType()):
                settings.Add(new Setting(prefix, secret ? Masked : formattable.ToString(null, CultureInfo.InvariantCulture), secret));
                return;
            case bool or Enum or Uri or TimeZoneInfo:
                settings.Add(new Setting(prefix, secret ? Masked : value.ToString() ?? NotSet, secret));
                return;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    Describe(entry.Value, $"{prefix}:{entry.Key}", secret, depth + 1, settings);
                }

                if (dictionary.Count == 0)
                {
                    settings.Add(new Setting(prefix, NotSet, secret));
                }

                return;
            case IEnumerable list:
                var items = list.Cast<object?>().ToList();
                if (items.Count == 0)
                {
                    settings.Add(new Setting(prefix, NotSet, secret));
                }
                else if (items.All(item => item is null or string || IsSimple(item.GetType()) || item is Enum))
                {
                    settings.Add(new Setting(prefix, secret ? Masked : string.Join(", ", items.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture))), secret));
                }
                else
                {
                    foreach (var (index, item) in items.Index())
                    {
                        Describe(item, $"{prefix}:{index}", secret, depth + 1, settings);
                    }
                }

                return;
        }

        if (depth >= MaxDepth)
        {
            return;
        }

        // What configuration binds: public properties that can be read and set.
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property is { CanRead: true, SetMethod.IsPublic: true } && property.GetIndexParameters().Length == 0))
        {
            Describe(property.GetValue(value), prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}", secret || SecretName().IsMatch(property.Name), depth + 1, settings);
        }
    }

    private static bool IsSimple(Type type) => type.IsPrimitive || type == typeof(decimal) || type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(DateTimeOffset) || type == typeof(DateTime);

    [GeneratedRegex("(.Keys?|Secrets?|Password|Token|Salt)$")]
    private static partial Regex SecretName();
}
