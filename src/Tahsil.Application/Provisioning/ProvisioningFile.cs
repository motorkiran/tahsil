using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Configuration;
using Tahsil.Contracts;
using Tahsil.Contracts.Serialization;

namespace Tahsil.Application.Provisioning;

/// <summary>
/// Reads a provisioning document written by people: comments and trailing commas are allowed, unknown members are not,
/// and enums are snake_case like everywhere in the API.
/// </summary>
public static class ProvisioningFile
{
    public static readonly JsonSerializerOptions SerializerOptions = TahsilJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    });

    public static async Task<ProvisioningDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProvisioningDocument>(stream, SerializerOptions, cancellationToken)
            ?? throw new InvalidOperationException($"The provisioning file '{path}' is empty.");
    }

    /// <summary>The JSON Schema of the document, which editors use for completion and validation.</summary>
    public static JsonNode Schema()
    {
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(
            SerializerOptions, typeof(ProvisioningDocument), new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });

        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = ProvisioningDocument.SchemaUrl,
            ["title"] = "Tahsil provisioning document",
        };

        foreach (var (name, value) in schema.AsObject().ToList())
        {
            root[name] = value?.DeepClone();
        }

        return root;
    }
}

/// <summary>From the <c>Tahsil:Provisioning</c> configuration section.</summary>
public sealed class ProvisioningOptions
{
    public const string SectionName = "Tahsil:Provisioning";

    /// <summary>A provisioning document applied whenever the database is prepared, such as at startup.</summary>
    public string? FilePath { get; set; }

    internal bool IsValid() => string.IsNullOrEmpty(FilePath) || File.Exists(FilePath);
}

/// <summary>
/// Applies the provisioning file of <c>Tahsil:Provisioning:FilePath</c> when the database is prepared, with its
/// credentials and secrets read from the configuration it refers to (PRV-7).
/// </summary>
public sealed class StartupProvisioning(ApplyProvisioningHandler apply, IConfiguration configuration)
{
    /// <summary>Returns a summary for the log; a document that cannot be applied stops startup.</summary>
    /// <exception cref="InvalidOperationException">The document cannot be applied; the message names the item.</exception>
    public async Task<string> ApplyAsync(string path, CancellationToken cancellationToken)
    {
        var document = ProvisioningSecrets.Resolve(await ProvisioningFile.ReadAsync(path, cancellationToken), name => configuration[name]);
        var result = document.IsFailure ? document.Error : await apply.HandleAsync(document.Value, dryRun: false, cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"The provisioning file '{path}' cannot be applied. {result.Error.Code}: {result.Error.Message}");
        }

        return $"{result.Value.Created} created, {result.Value.Updated} updated, {result.Value.Unchanged} unchanged";
    }
}
