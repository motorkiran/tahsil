using System.Reflection;

namespace Tahsil.Architecture.Tests;

/// <summary>
/// Enforces the dependency rules of the design document (section 6.2). The compiler only records references that
/// code actually uses, so these checks catch real dependencies, not project files. Rules match assemblies by name,
/// which covers projects that do not exist yet.
/// </summary>
[Trait("Requirement", "FND-54")]
public sealed class DependencyRuleTests
{
    private static readonly string[] ProjectFolders = ["src", "sdk"];

    [Fact]
    [Trait("Requirement", "FND-2")]
    public void TheFoundationProjectsAreChecked()
    {
        string[] names = [.. TahsilAssemblies.Loaded.Select(assembly => assembly.GetName().Name!)];

        names.ShouldContain("Tahsil.Domain");
        names.ShouldContain("Tahsil.Application");
        names.ShouldContain("Tahsil.Infrastructure");
        names.ShouldContain("Tahsil.Server");
        names.ShouldContain("Tahsil.Providers.Abstractions");
    }

    [Fact]
    public void EveryProjectInTheRepositoryIsChecked()
    {
        var projects = ProjectFolders
            .Select(folder => Path.Combine(TahsilAssemblies.RepositoryRoot, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories))
            .Select(Path.GetFileNameWithoutExtension);
        string[] loaded = [.. TahsilAssemblies.Loaded.Select(assembly => assembly.GetName().Name!)];

        foreach (var project in projects)
        {
            loaded.ShouldContain(project, $"Reference {project} from the architecture tests so its rules are checked.");
        }
    }

    [Fact]
    public void Domain_DependsOnNoOtherProjectOrFramework()
    {
        Check(
            "Tahsil.Domain",
            name => !TahsilAssemblies.IsTahsil(name)
                && !name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                && !name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                && !name.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Contracts_DependOnNothingButTheRuntime()
    {
        CheckAll(name => name == "Tahsil.Contracts", IsRuntime);
    }

    [Fact]
    public void ProviderAbstractions_DependOnTheRuntimeAndExtensionAbstractionsOnly()
    {
        Check("Tahsil.Providers.Abstractions", name => IsRuntime(name) || IsExtensionAbstraction(name));
    }

    [Fact]
    public void ProviderAdapters_KnowOnlyTheAdapterContract()
    {
        CheckAll(
            IsProviderAdapter,
            name => TahsilAssemblies.IsTahsil(name)
                ? name == "Tahsil.Providers.Abstractions"
                : !IsWebOrDatabase(name));
    }

    [Fact]
    public void Application_DependsOnTheModelAndPortsOnly()
    {
        Check(
            "Tahsil.Application",
            name => TahsilAssemblies.IsTahsil(name)
                ? name is "Tahsil.Domain" or "Tahsil.Contracts" or "Tahsil.Providers.Abstractions"
                : !name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                    && !name.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnTheHostOrAnAdapter()
    {
        Check(
            "Tahsil.Infrastructure",
            name => !TahsilAssemblies.IsTahsil(name)
                || name is "Tahsil.Application" or "Tahsil.Domain" or "Tahsil.Providers.Abstractions");
    }

    [Fact]
    public void AdminPanel_GoesThroughTheApplication()
    {
        CheckAll(
            name => name == "Tahsil.Admin",
            name => !TahsilAssemblies.IsTahsil(name) || name is "Tahsil.Application" or "Tahsil.Contracts" or "Tahsil.Domain");
    }

    [Fact]
    public void Client_SharesOnlyTheContracts()
    {
        CheckAll(
            name => name == "Tahsil.Client",
            name => !TahsilAssemblies.IsTahsil(name) || name == "Tahsil.Contracts");
    }

    [Fact]
    public void AspNetCore_SharesOnlyTheContractsAndTheClient()
    {
        CheckAll(
            name => name == "Tahsil.AspNetCore",
            name => !TahsilAssemblies.IsTahsil(name) || name is "Tahsil.Contracts" or "Tahsil.Client");
    }

    private static bool IsProviderAdapter(string name) =>
        name.StartsWith("Tahsil.Providers.", StringComparison.Ordinal) && name != "Tahsil.Providers.Abstractions";

    private static bool IsRuntime(string name) =>
        name is "System" or "mscorlib" or "netstandard" || name.StartsWith("System.", StringComparison.Ordinal);

    private static bool IsExtensionAbstraction(string name) =>
        name is "Microsoft.Extensions.Http" or "Microsoft.Extensions.Options" or "Microsoft.Extensions.Primitives"
            || (name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
                && name.EndsWith(".Abstractions", StringComparison.Ordinal));

    private static bool IsWebOrDatabase(string name) =>
        name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || name.StartsWith("Npgsql", StringComparison.Ordinal);

    private static void Check(string assemblyName, Func<string, bool> allowed) =>
        CheckAssembly(TahsilAssemblies.Named(assemblyName), allowed);

    private static void CheckAll(Func<string, bool> assemblyName, Func<string, bool> allowed)
    {
        foreach (var assembly in TahsilAssemblies.Matching(assemblyName))
        {
            CheckAssembly(assembly, allowed);
        }
    }

    private static void CheckAssembly(Assembly assembly, Func<string, bool> allowed)
    {
        string[] forbidden = [.. TahsilAssemblies.ReferencedAssemblies(assembly).Where(name => !allowed(name))];

        forbidden.ShouldBeEmpty($"{assembly.GetName().Name} has references the design does not allow.");
    }
}
