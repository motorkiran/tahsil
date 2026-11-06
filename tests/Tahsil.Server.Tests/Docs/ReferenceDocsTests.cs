using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tahsil.Server.Tests.Docs;

/// <summary>
/// The reference pages are generated from the code, so that they cannot fall behind it: the configuration reference
/// from the options classes and their documentation comments (OPS §1), the API reference from the OpenAPI snapshot.
/// </summary>
public sealed partial class ReferenceDocsTests
{
    /// <summary>Set to rewrite the pages after an intended change.</summary>
    private const string UpdateVariable = "TAHSIL_UPDATE_SNAPSHOTS";

    private static readonly string Repository = Path.GetFullPath(Path.Combine(
        typeof(ReferenceDocsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(metadata => metadata.Key == "ProjectDirectory").Value!, "..", ".."));

    [Fact]
    [Trait("Requirement", "OPS-1")]
    public Task TheConfigurationReference_IsUpToDate() => CheckAsync("configuration.md", ConfigurationReference());

    [Fact]
    [Trait("Requirement", "FND-54")]
    public Task TheApiReference_IsUpToDate() => CheckAsync("api.md", ApiReference());

    private static async Task CheckAsync(string name, string generated)
    {
        var path = Path.Combine(Repository, "docs", "reference", name);
        if (Environment.GetEnvironmentVariable(UpdateVariable) is not null)
        {
            await File.WriteAllTextAsync(path, generated, TestContext.Current.CancellationToken);
        }

        File.Exists(path).ShouldBeTrue($"No {name} yet; run the tests with {UpdateVariable}=1 to create it.");
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n").ShouldBe(
            generated, $"docs/reference/{name} is out of date: run the tests with {UpdateVariable}=1 and commit it.");
    }

    private static string ConfigurationReference()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is { } name && name.StartsWith("Tahsil.", StringComparison.Ordinal) && !name.EndsWith(".Tests", StringComparison.Ordinal))
            .ToList();
        var docs = Documentation(assemblies);
        var types = assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && SectionOf(type) is not null && type.GetConstructor(Type.EmptyTypes) is not null)
            .GroupBy(type => SectionOf(type)!)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        var text = new StringBuilder();
        text.Append("""
            # Configuration reference

            <!-- Generated from the options classes by ReferenceDocsTests; do not edit. Run the tests with TAHSIL_UPDATE_SNAPSHOTS=1 after changing them. -->

            Tahsil reads its configuration at startup, from `appsettings.json`, then from environment variables, which win.
            A setting such as `Tahsil:Server:PublicUrl` is the environment variable `Tahsil__Server__PublicUrl`; an item of a
            list is numbered (`Tahsil__Dunning__RetrySchedule__0`), and an entry of a dictionary is named by its key. Durations
            are written `d.hh:mm:ss`, such as `00:05:00` for five minutes or `3.00:00:00` for three days. Invalid settings stop
            Tahsil at startup with a message that names them. The admin panel's Settings page shows the values an installation
            runs with, secrets masked.

            The database is `ConnectionStrings:Tahsil` (`ConnectionStrings__Tahsil`), a PostgreSQL connection string.

            """);
        foreach (var section in types)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n## `{section.Key}`\n\n");
            foreach (var type in section.OrderBy(type => type.Name, StringComparer.Ordinal))
            {
                if (Summary(docs, $"T:{type.FullName}") is { Length: > 0 } about)
                {
                    text.Append(about).Append("\n\n");
                }
            }

            text.Append("| Setting | Default | Description |\n|---|---|---|\n");
            var rows = new List<(string Key, string Default, string Description)>();
            foreach (var type in section)
            {
                Describe(type, Activator.CreateInstance(type), string.Empty, docs, rows, depth: 0);
            }

            foreach (var (key, @default, description) in rows.DistinctBy(row => row.Key).OrderBy(row => row.Key, StringComparer.Ordinal))
            {
                text.Append(CultureInfo.InvariantCulture, $"| `{key}` | {@default} | {description} |\n");
            }
        }

        return text.ToString();
    }

    private static void Describe(Type type, object? instance, string prefix, Dictionary<string, string> docs, List<(string, string, string)> rows, int depth)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property is { CanRead: true, SetMethod.IsPublic: true } && property.GetIndexParameters().Length == 0))
        {
            var key = prefix + property.Name;
            var description = Summary(docs, $"P:{property.DeclaringType!.FullName}.{property.Name}") ?? string.Empty;
            var value = instance is null ? null : property.GetValue(instance);
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (IsLeaf(propertyType))
            {
                rows.Add((key, Default(value), description));
            }
            else if (propertyType.IsGenericType && typeof(IDictionary).IsAssignableFrom(propertyType))
            {
                var item = propertyType.GetGenericArguments()[1];
                rows.Add((key, value is IDictionary { Count: > 0 } map ? string.Join(", ", map.Keys.Cast<object>()) : "–", description));
                if (!IsLeaf(item) && depth < 3)
                {
                    Describe(item, null, $"{key}:{{key}}:", docs, rows, depth + 1);
                }
            }
            else if (propertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(propertyType))
            {
                var item = propertyType.IsArray ? propertyType.GetElementType()! : propertyType.GetGenericArguments().FirstOrDefault() ?? typeof(object);
                var items = value is IEnumerable list ? list.Cast<object?>().ToList() : [];
                rows.Add((key, IsLeaf(item) && items.Count > 0 ? string.Join(", ", items.Select(Default)) : "–", description));
                if (!IsLeaf(item) && depth < 3)
                {
                    Describe(item, null, $"{key}:{{n}}:", docs, rows, depth + 1);
                }
            }
            else if (depth < 3)
            {
                if (description.Length > 0)
                {
                    rows.Add((key, string.Empty, description));
                }

                Describe(propertyType, value, key + ":", docs, rows, depth + 1);
            }
        }
    }

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(TimeSpan) || type == typeof(Uri)
        || type == typeof(DateTimeOffset) || type == typeof(Guid);

    private static string Default(object? value) => value switch
    {
        null => "–",
        string { Length: 0 } => "–",
        string text => $"`{text}`",
        bool flag => flag ? "`true`" : "`false`",
        TimeSpan span => $"`{span.ToString("c", CultureInfo.InvariantCulture)}`",
        Enum choice => $"`{choice}`",
        IFormattable formattable => $"`{formattable.ToString(null, CultureInfo.InvariantCulture)}`",
        _ => $"`{value}`",
    };

    private static string? SectionOf(Type type) =>
        type.GetField("SectionName", BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } field && field.FieldType == typeof(string)
            ? (string?)field.GetRawConstantValue()
            : null;

    /// <summary>The documentation comments of the assemblies, by member ID.</summary>
    private static Dictionary<string, string> Documentation(IEnumerable<Assembly> assemblies)
    {
        var docs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            var file = Path.ChangeExtension(assembly.Location, ".xml");
            if (!File.Exists(file))
            {
                continue;
            }

            foreach (var member in XDocument.Load(file).Descendants("member"))
            {
                if (member.Element("summary") is { } summary)
                {
                    docs[member.Attribute("name")!.Value] = Markdown(summary);
                }
            }
        }

        return docs;
    }

    private static string? Summary(Dictionary<string, string> docs, string id) => docs.GetValueOrDefault(id);

    /// <summary>A documentation comment as one line of Markdown.</summary>
    private static string Markdown(XElement summary)
    {
        var text = new StringBuilder();
        foreach (var node in summary.Nodes())
        {
            text.Append(node switch
            {
                XText plain => plain.Value,
                XElement { Name.LocalName: "c" } code => $"`{code.Value}`",
                XElement { Name.LocalName: "see" } see => see.Attribute("langword")?.Value is { } word ? $"`{word}`" : ShortName(see.Attribute("cref")?.Value),
                XElement { Name.LocalName: "paramref" or "typeparamref" } reference => $"`{reference.Attribute("name")?.Value}`",
                XElement element => element.Value,
                _ => string.Empty,
            });
        }

        // Requirement IDs point into the design documents, which are not published.
        var line = Requirements().Replace(Whitespace().Replace(text.ToString(), " "), string.Empty);
        return line.Trim().Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static string ShortName(string? cref) => cref is null ? string.Empty : $"`{cref[(cref.LastIndexOf('.') + 1)..]}`";

    private static string ApiReference()
    {
        var path = Path.Combine(Repository, "tests", "Tahsil.Server.Tests", "Api", "openapi-v1.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        var text = new StringBuilder();
        text.Append("""
            # API reference

            <!-- Generated from the OpenAPI snapshot by ReferenceDocsTests; do not edit. Run the tests with TAHSIL_UPDATE_SNAPSHOTS=1 after changing the API. -->

            Every endpoint of the Tahsil API, under `/api/v1`, grouped as the OpenAPI document groups them. An installation serves
            the document itself at `/openapi/v1.json`, and an interactive reference at `/scalar/v1` (both on by default in Test
            mode; `Tahsil:Api:PublicDocumentation` turns them on or off). Request and response bodies are described there;
            [the API guide](../guides/api.md) explains authentication, idempotency keys, errors, paging and versions.

            """);
        var operations = document["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(operation => (Path: path.Key, Method: operation.Key.ToUpperInvariant(), Operation: operation.Value!)))
            .GroupBy(entry => entry.Operation["tags"]?[0]?.GetValue<string>() ?? "Other");
        foreach (var tag in operations)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n## {tag.Key}\n");
            foreach (var (route, method, operation) in tag)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n### {operation["summary"]?.GetValue<string>()}\n\n`{method} {route}`\n\n");
                if (operation["description"]?.GetValue<string>() is { Length: > 0 } description)
                {
                    text.Append(description.Trim()).Append("\n\n");
                }

                var parameters = (operation["parameters"]?.AsArray() ?? []).Where(parameter => parameter!["in"]!.GetValue<string>() == "query").ToList();
                if (parameters.Count > 0)
                {
                    text.Append("| Parameter | Description |\n|---|---|\n");
                    foreach (var parameter in parameters)
                    {
                        var required = parameter!["required"]?.GetValue<bool>() == true ? " (required)" : string.Empty;
                        var about = parameter["description"]?.GetValue<string>() ?? string.Empty;
                        text.Append(CultureInfo.InvariantCulture, $"| `{parameter["name"]}`{required} | {about.Replace("|", "\\|", StringComparison.Ordinal)} |\n");
                    }

                    text.Append('\n');
                }

                var body = operation["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>();
                var answer = operation["responses"]!.AsObject().FirstOrDefault(response => response.Key.StartsWith('2'));
                var result = answer.Value?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>();
                if (body is not null || answer.Key is not null)
                {
                    text.Append(CultureInfo.InvariantCulture, $"Request: {(body is null ? "none" : $"`{body[(body.LastIndexOf('/') + 1)..]}`")}. ");
                    text.Append(CultureInfo.InvariantCulture, $"Response: `{answer.Key}`{(result is null ? string.Empty : $" `{result[(result.LastIndexOf('/') + 1)..]}`")}.\n");
                }
            }
        }

        return text.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*\((?:[A-Z]{2,4}(?:-\d+| §[\d.]+))(?:(?:,| and| to) (?:[A-Z]{2,4}(?:-\d+| §[\d.]+)|§[\d.]+))*\)")]
    private static partial Regex Requirements();
}
