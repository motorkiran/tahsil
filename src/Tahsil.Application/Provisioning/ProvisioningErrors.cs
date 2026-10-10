using Tahsil.Domain.Common;

namespace Tahsil.Application.Provisioning;

public static class ProvisioningErrors
{
    /// <summary>A price's amount, a tax rate's percentage and the like identify what bills rely on and cannot change.</summary>
    public static Error FixedProperty(string property) => Error.Validation(
        "provisioning.fixed_property", $"'{property}' cannot change once created; declare a new item with another key instead.", property);

    public static readonly Error LiteralSecret = Error.Validation(
        "provisioning.literal_secret", "Credentials and secrets in a provisioning file are configuration references such as ${Iyzico:ApiKey}, so secrets never live in version control.");

    public static readonly Error ConfigReferenceNotAllowed = Error.Validation(
        "provisioning.config_reference_not_allowed", "Documents sent through the API give credentials and secrets literally or omit them; ${…} references are for the startup file.");

    /// <summary>The error of one item in the document, with where to find it: <c>products[0].prices[2] 'api-calls-try': …</c>.</summary>
    internal static Error At(string path, string key, Error error) => error with { Message = $"{path} '{key}': {error.Message}" };
}

/// <summary>Compares declared values with stored ones the way Tahsil stores them.</summary>
internal static class Declared
{
    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool SameMetadata(IReadOnlyDictionary<string, string> stored, IReadOnlyDictionary<string, string>? declared) =>
        stored.Count == (declared?.Count ?? 0) && stored.All(pair => declared!.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
