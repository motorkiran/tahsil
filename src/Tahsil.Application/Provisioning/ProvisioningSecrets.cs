using System.Text.RegularExpressions;
using Tahsil.Contracts;
using Tahsil.Domain.Common;

namespace Tahsil.Application.Provisioning;

/// <summary>
/// Credentials and webhook secrets in provisioning documents (PRV-7, PRV-9): the startup file names them as
/// configuration references, <c>${Section:Key}</c>, resolved from Tahsil's configuration; documents sent through the API
/// give them literally, since API callers must not read the server's configuration.
/// </summary>
public static partial class ProvisioningSecrets
{
    /// <summary>Replaces the startup file's references with their configured values.</summary>
    /// <param name="document">The startup file's document.</param>
    /// <param name="configuration">Reads a configuration value by its key, such as <c>Iyzico:ApiKey</c>.</param>
    /// <exception cref="InvalidOperationException">A referenced value is not configured: startup cannot go on.</exception>
    public static Result<ProvisioningDocument> Resolve(ProvisioningDocument document, Func<string, string?> configuration)
    {
        var connections = new List<ProvisionedConnection>();
        foreach (var (index, connection) in (document.ProviderConnections ?? []).Index())
        {
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (field, value) in connection.Credentials ?? new Dictionary<string, string>())
            {
                var path = $"providerConnections[{index}].credentials.{field}";
                if (Reference(value) is not { } name)
                {
                    return ProvisioningErrors.At(path, connection.Key, ProvisioningErrors.LiteralSecret);
                }

                credentials[field] = configuration(name) ?? throw Missing(path, name);
            }

            connections.Add(connection with { Credentials = connection.Credentials is null ? null : credentials });
        }

        var endpoints = new List<ProvisionedWebhookEndpoint>();
        foreach (var (index, endpoint) in (document.WebhookEndpoints ?? []).Index())
        {
            var path = $"webhookEndpoints[{index}].secret";
            if (endpoint.Secret is null)
            {
                endpoints.Add(endpoint);
                continue;
            }

            if (Reference(endpoint.Secret) is not { } name)
            {
                return ProvisioningErrors.At(path, endpoint.Key, ProvisioningErrors.LiteralSecret);
            }

            endpoints.Add(endpoint with { Secret = configuration(name) ?? throw Missing(path, name) });
        }

        return document with
        {
            ProviderConnections = document.ProviderConnections is null ? null : connections,
            WebhookEndpoints = document.WebhookEndpoints is null ? null : endpoints,
        };
    }

    /// <summary>The first configuration reference in a document sent through the API, which is not allowed there.</summary>
    public static Error? FindReference(ProvisioningDocument document)
    {
        foreach (var (index, connection) in (document.ProviderConnections ?? []).Index())
        {
            if (connection.Credentials?.FirstOrDefault(field => Reference(field.Value) is not null) is { Key: { } field })
            {
                return ProvisioningErrors.At($"providerConnections[{index}].credentials.{field}", connection.Key, ProvisioningErrors.ConfigReferenceNotAllowed);
            }
        }

        foreach (var (index, endpoint) in (document.WebhookEndpoints ?? []).Index())
        {
            if (Reference(endpoint.Secret) is not null)
            {
                return ProvisioningErrors.At($"webhookEndpoints[{index}].secret", endpoint.Key, ProvisioningErrors.ConfigReferenceNotAllowed);
            }
        }

        return null;
    }

    /// <summary>The reference an export puts in place of a stored secret, to be configured where the export is applied.</summary>
    public static string ReferenceTo(string name) => $"${{{name}}}";

    private static string? Reference(string? value) => value is null ? null : ReferencePattern().Match(value.Trim()) is { Success: true } match ? match.Groups["name"].Value : null;

    private static InvalidOperationException Missing(string path, string name) =>
        new($"The provisioning file's {path} refers to the configuration value '{name}', which is not set (environment variable {name.Replace(":", "__", StringComparison.Ordinal)}).");

    [GeneratedRegex(@"^\$\{(?<name>[A-Za-z0-9_.:-]+)\}$")]
    private static partial Regex ReferencePattern();
}
