namespace Tahsil.Application.Common;

/// <summary>Audit data for updates: the names of the fields a request changed.</summary>
internal static class Changes
{
    public static IReadOnlyDictionary<string, object?> Of(params (string Field, bool Changed)[] fields) =>
        new Dictionary<string, object?> { ["fields"] = fields.Where(field => field.Changed).Select(field => field.Field).ToArray() };
}
