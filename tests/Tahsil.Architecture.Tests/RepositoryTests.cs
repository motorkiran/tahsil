using System.Text.Json.Nodes;

namespace Tahsil.Architecture.Tests;

public sealed class RepositoryTests
{
    [Theory]
    [Trait("Requirement", "FND-1")]
    [InlineData("Tahsil.slnx")]
    [InlineData("global.json")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Packages.props")]
    [InlineData(".editorconfig")]
    [InlineData("tests/.editorconfig")]
    [InlineData(".config/dotnet-tools.json")]
    [InlineData("compose.yaml")]
    [InlineData("README.md")]
    [InlineData("CHANGELOG.md")]
    [InlineData("LICENSE")]
    [InlineData("SECURITY.md")]
    [InlineData(".github/dependabot.yml")]
    [InlineData(".github/workflows/ci.yml")]
    public void TheRepositoryRoot_HasItsFiles(string path)
    {
        File.Exists(Path.Combine(TahsilAssemblies.RepositoryRoot, path)).ShouldBeTrue();
    }

    [Fact]
    [Trait("Requirement", "FND-3")]
    public void AssemblyVersions_AreFixed()
    {
        // Data Protection stores assembly-qualified type names, which must survive upgrades.
        TahsilAssemblies.Loaded.ShouldAllBe(assembly => assembly.GetName().Version == new Version(1, 0, 0, 0));
    }

    [Fact]
    [Trait("Requirement", "FND-7")]
    public void TheSdk_IsPinned_AndTestsRunOnTheTestingPlatform()
    {
        var globalJson = JsonNode.Parse(File.ReadAllText(Path.Combine(TahsilAssemblies.RepositoryRoot, "global.json")))!;

        globalJson["sdk"]!["version"]!.GetValue<string>().ShouldBe("10.0.100");
        globalJson["sdk"]!["rollForward"]!.GetValue<string>().ShouldBe("latestFeature");
        globalJson["test"]!["runner"]!.GetValue<string>().ShouldBe("Microsoft.Testing.Platform");
        typeof(RepositoryTests).Assembly.EntryPoint.ShouldNotBeNull();
    }
}
