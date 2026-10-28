using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Provisioning documents: the catalog, connections and endpoints as code. Requires <c>tahsil.provisioning:write</c>.</summary>
public sealed class ProvisioningClient
{
    private readonly TahsilHttp _http;

    internal ProvisioningClient(TahsilHttp http) => _http = http;

    /// <summary>Export the provisioning document. The current catalog, ordered by key, in the format that POST applies.</summary>
    public Task<ProvisioningDocument> ExportAsync(RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ProvisioningDocument>(HttpMethod.Get, "provisioning", null, options, cancellationToken);

    /// <summary>Apply a provisioning document. Creates what is missing and updates what differs, in one transaction, and never deletes. Each item goes through the same rules as the API, with the caller's permissions; an error names the item and nothing is applied.</summary>
    /// <param name="request">What to send.</param>
    /// <param name="dryRun">Validates the document and reports what would change, without changing anything.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ProvisioningResult> ApplyAsync(ProvisioningDocument request, bool? dryRun = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ProvisioningResult>(HttpMethod.Post, TahsilHttp.WithQuery("provisioning", ("dryRun", dryRun)), request, options, cancellationToken);
}
