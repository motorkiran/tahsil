using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tahsil.Server.Tests.HostedPages;

/// <summary>
/// The hosted pages are written in English, and every text they show has a Turkish translation, which nothing else
/// is left in.
/// </summary>
[Trait("Requirement", "FND-41")]
public sealed partial class TranslationTests
{
    private static readonly string[] Folders = ["Pages", "Ui"];

    [Fact]
    public void EveryText_HasATurkishTranslation()
    {
        var project = typeof(TranslationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(metadata => metadata.Key == "ProjectDirectory").Value!;
        var server = Path.GetFullPath(Path.Combine(project, "..", "..", "src", "Tahsil.Server"));

        var translated = XDocument.Load(Path.Combine(server, "Resources", "SharedResource.tr.resx")).Root!
            .Elements("data")
            .Select(data => data.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);

        var texts = Folders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(server, folder), "*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".cshtml", StringComparison.Ordinal))
            .SelectMany(file => LocalizedText().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["text"].Value)
            .ToHashSet(StringComparer.Ordinal);

        texts.ShouldNotBeEmpty();
        texts.Where(text => !translated.Contains(text)).Order(StringComparer.Ordinal).ShouldBeEmpty("Texts without a translation");
        translated.Where(text => !texts.Contains(text)).Order(StringComparer.Ordinal).ShouldBeEmpty("Translations no page uses");
    }

    /// <summary>Texts passed to the localizer in pages and code, and the display names and messages of form fields.</summary>
    [GeneratedRegex("""(?:\bL\[|\blocalizer\[|Display\(Name = |ErrorMessage = )"(?<text>[^"]+)"[\],)]""")]
    private static partial Regex LocalizedText();
}
