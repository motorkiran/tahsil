using System.Reflection;

namespace Tahsil.Architecture.Tests;

/// <summary>
/// The Tahsil assemblies built next to the tests, found by name, so that projects added later are checked by the
/// rules that match their name without changing the tests.
/// </summary>
internal static class TahsilAssemblies
{
    private static readonly Lazy<Assembly[]> All = new(Load);

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static IReadOnlyList<Assembly> Loaded => All.Value;

    public static Assembly Named(string name) =>
        Loaded.SingleOrDefault(assembly => assembly.GetName().Name == name)
            ?? throw new InvalidOperationException($"The assembly '{name}' was not found next to the tests.");

    public static IEnumerable<Assembly> Matching(Func<string, bool> name) =>
        Loaded.Where(assembly => name(assembly.GetName().Name!));

    public static string[] ReferencedAssemblies(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];

    public static string[] TahsilReferences(Assembly assembly) =>
        [.. ReferencedAssemblies(assembly).Where(IsTahsil)];

    public static bool IsTahsil(string name) => name.StartsWith("Tahsil.", StringComparison.Ordinal);

    private static Assembly[] Load() =>
        [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "Tahsil.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith(".Tests", StringComparison.Ordinal))
            .Select(name => Assembly.Load(new AssemblyName(name!)))
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)];

    private static string FindRepositoryRoot()
    {
        var projectDirectory = typeof(TahsilAssemblies).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ProjectDirectory")
            .Value!;

        return Path.GetFullPath(Path.Combine(projectDirectory, "..", ".."));
    }
}
