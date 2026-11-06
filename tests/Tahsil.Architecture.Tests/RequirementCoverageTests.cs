using System.Text.RegularExpressions;

namespace Tahsil.Architecture.Tests;

/// <summary>
/// The requirement coverage gate: every requirement of the specifications is named by a test, by trait
/// (<c>[Trait("Requirement", "PAY-12")]</c>) or display name, or listed here as verified otherwise or deferred. The
/// specifications are internal and not in the public repository, so the gate is skipped where they are missing.
/// </summary>
[Trait("Requirement", "FND-55")]
public sealed partial class RequirementCoverageTests
{
    /// <summary>Requirements that the build, CI, the repository's files or the guides verify rather than a test.</summary>
    private static readonly Dictionary<string, string> VerifiedOtherwise = new(StringComparer.Ordinal)
    {
        ["CHK-33"] = "The checkout page binds only the customer's details, consents and a promotion code; amounts come from the session's quote.",
        ["CRG-2"] = "Operator guide: docs/guides/providers/craftgate.md; the admin panel shows each connection's notification URL.",
        ["DOM-16"] = "Database check constraint ck_payments_subject.",
        ["EVT-6"] = "Compatibility policy; event changes show in the event catalog and its generated reference.",
        ["FND-4"] = "Directory.Packages.props: central package management with transitive pinning; restore enforces it.",
        ["FND-5"] = ".editorconfig; CI runs dotnet format --verify-no-changes and builds with warnings as errors.",
        ["FND-16"] = "Guid.NewGuid is banned in src (BannedSymbols.txt); ids come from Guid.CreateVersion7(now).",
        ["FND-35"] = "The clock's static properties are banned in src (BannedSymbols.txt); time comes from TimeProvider.",
        ["FND-47"] = "The release workflow builds and publishes the container image.",
        ["FND-48"] = "compose.yaml.",
        ["FND-49"] = ".github/workflows/ci.yml and codeql.yml.",
        ["FND-50"] = ".github/workflows/release.yml.",
        ["IYZ-1"] = "Operator guide: docs/guides/providers/iyzico.md; the admin panel shows each connection's notification URL.",
        ["KML-1"] = "AdminAuthentication's OpenID Connect settings; signing in was verified against Kimlik end to end.",
        ["KML-7"] = "AdminAuthentication's sign-out; verified against Kimlik end to end.",
        ["KML-8"] = "AdminOptions' defaults and samples/kimlik/provisioning.json.",
        ["KML-10"] = "Kimlik's own setting; docs/guides/concepts/kimlik.md recommends it.",
        ["KML-25"] = "compose.yaml (profile kimlik).",
        ["MNY-25"] = "The clock's static properties are banned in src (BannedSymbols.txt).",
        ["NTF-15"] = "compose.yaml runs Mailpit; the development configuration sends to it.",
        ["OPS-3"] = "docs/guides/operations/deployment.md.",
        ["OPS-5"] = "docs/guides/operations/deployment.md.",
        ["OPS-6"] = "docs/guides/operations/deployment.md and go-live.md.",
        ["OPS-7"] = "docs/guides/operations/deployment.md.",
        ["OPS-8"] = "docs/guides/operations/deployment.md and go-live.md.",
        ["OPS-9"] = "docs/guides/operations/runbooks.md; reconciliation itself is tested.",
        ["OPS-10"] = "docs/guides/operations/deployment.md.",
        ["PAY-9"] = "A payment method cannot exist without its consent record (PaymentMethod.ConsentRecordId is required).",
        ["PTR-1"] = "Operator guide: docs/guides/providers/paytr.md; the admin panel shows each connection's notification URL.",
        ["SDK-13"] = "The SDK projects' csproj and README files; the release workflow publishes them once the prefix is reserved.",
        ["SEC-2"] = "See CHK-33.",
        ["SEC-11"] = "CodeQL in CI; raw SQL only from constant text with parameters; anti-forgery on every form.",
        ["SEC-17"] = "Dependabot, CodeQL, dependency review, the SBOM and attestations in the workflows.",
        ["SEC-19"] = "docs/guides/operations/deployment.md.",
        ["SIM-10"] = "The M2 migration; Live mode hides the simulator (tested with the connections).",
        ["STR-2"] = "Directory.Packages.props pins Stripe.net; StripeProvider makes one client per connection.",
        ["SUB-36"] = "The API has no fixed term; the starter legal documents say so.",
    };

    /// <summary>Requirements that wait for the product owner's decision before they are built or tested; none now.</summary>
    private static readonly Dictionary<string, string> Deferred = new(StringComparer.Ordinal);

    [Fact]
    public void EveryRequirement_IsTested_OrAccountedFor()
    {
        var specifications = Path.Combine(TahsilAssemblies.RepositoryRoot, "docs", "specs");
        Assert.SkipUnless(Directory.Exists(specifications), "The specifications are not in this checkout.");

        var defined = Directory.EnumerateFiles(specifications, "*.md", SearchOption.AllDirectories)
            .SelectMany(file => Definition().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var tested = Directory.EnumerateFiles(Path.Combine(TahsilAssemblies.RepositoryRoot, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // This file's lists name requirements too; only its trait counts.
            .SelectMany(file => (file.EndsWith(nameof(RequirementCoverageTests) + ".cs", StringComparison.Ordinal) ? Trait() : Reference()).Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var listed = VerifiedOtherwise.Keys.Concat(Deferred.Keys).ToList();
        var report = new List<string> { $"{defined.Count} requirements, {defined.Count(tested.Contains)} named by tests, {VerifiedOtherwise.Count} verified otherwise, {Deferred.Count} deferred:" };
        report.AddRange(Deferred.Select(item => $"  {item.Key}: {item.Value}"));

        // Test output only shows for failures, so the report is also written next to the test binaries.
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "requirement-coverage.txt"), report);
        foreach (var line in report)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }

        defined.ShouldNotBeEmpty();
        defined.Except(tested).Except(listed).Order(RequirementOrder.Instance).ShouldBeEmpty("Requirements that no test names and that are not listed here");
        listed.Where(tested.Contains).ShouldBeEmpty("Listed requirements that tests now name: take them off the list");
        listed.Except(defined).ShouldBeEmpty("Listed requirements that the specifications do not define");
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

    /// <summary>A requirement trait, the only reference this file's own text makes to a requirement.</summary>
    [GeneratedRegex(@"\[Trait\(""Requirement"", ""(?<id>[A-Z]{2,4}-\d+)""\)\]")]
    private static partial Regex Trait();

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
