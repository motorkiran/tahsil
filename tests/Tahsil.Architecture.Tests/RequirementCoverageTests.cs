using System.Text.RegularExpressions;

namespace Tahsil.Architecture.Tests;

/// <summary>
/// Lists the requirements of the specifications that no test names yet, by trait (<c>[Trait("Requirement", "PAY-12")]</c>)
/// or display name. A report until M12, where it becomes a gate for MUST requirements. The specifications are
/// internal and not in the public repository, so the report is skipped where they are missing.
/// </summary>
[Trait("Requirement", "FND-55")]
public sealed partial class RequirementCoverageTests
{
    [Fact]
    public void ReportsRequirementsWithoutTests()
    {
        var specifications = Path.Combine(TahsilAssemblies.RepositoryRoot, "docs", "specs");
        Assert.SkipUnless(Directory.Exists(specifications), "The specifications are not in this checkout.");

        var defined = Directory.EnumerateFiles(specifications, "*.md", SearchOption.AllDirectories)
            .SelectMany(file => Definition().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var tested = Directory.EnumerateFiles(Path.Combine(TahsilAssemblies.RepositoryRoot, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => Reference().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var untested = defined.Except(tested).Order(RequirementOrder.Instance).ToList();
        var report = new List<string> { $"{defined.Count} requirements, {defined.Count - untested.Count} named by tests, {untested.Count} not yet:" };
        report.AddRange(untested
            .GroupBy(id => id[..id.IndexOf('-', StringComparison.Ordinal)])
            .Select(prefix => $"  {prefix.Key}: {string.Join(", ", prefix)}"));

        // Test output only shows for failures, so the report is also written next to the test binaries.
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "requirement-coverage.txt"), report);
        foreach (var line in report)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }

        defined.ShouldNotBeEmpty();
        tested.Except(defined).ShouldBeEmpty("Tests name requirements that the specifications do not define");
    }

    /// <summary>A requirement's definition: <c>**FND-3:**</c> or <c>**FND-3 (build settings):**</c>.</summary>
    [GeneratedRegex(@"\*\*(?<id>[A-Z]{2,4}-\d+)[\s:(*]")]
    private static partial Regex Definition();

    /// <summary>
    /// A requirement ID inside a string literal in a test, such as a trait value or a display name; not a longer
    /// identifier that starts like one, such as the invoice number <c>INV-2026-000001</c>.
    /// </summary>
    [GeneratedRegex(@"""[^""\n]*?\b(?<id>[A-Z]{2,4}-\d+)\b(?!-)")]
    private static partial Regex Reference();

    /// <summary>By prefix, then number: FND-2 before FND-10.</summary>
    private sealed class RequirementOrder : IComparer<string>
    {
        public static readonly RequirementOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var (xPrefix, xNumber) = Split(x!);
            var (yPrefix, yNumber) = Split(y!);
            var byPrefix = string.CompareOrdinal(xPrefix, yPrefix);
            return byPrefix != 0 ? byPrefix : xNumber.CompareTo(yNumber);
        }

        private static (string Prefix, int Number) Split(string id)
        {
            var dash = id.IndexOf('-', StringComparison.Ordinal);
            return (id[..dash], int.Parse(id[(dash + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
