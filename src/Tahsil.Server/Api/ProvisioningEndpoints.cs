using System.ComponentModel;
using Tahsil.Application.Provisioning;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>The catalog as code: apply a provisioning document, or export the current one.</summary>
internal static class ProvisioningEndpoints
{
    public static IEndpointRouteBuilder MapProvisioningEndpoints(this IEndpointRouteBuilder api)
    {
        var provisioning = api.MapGroup("provisioning").WithTags("Provisioning");

        provisioning.MapGet(string.Empty, async (ExportProvisioningHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(cancellationToken)).ToOk())
            .WithName("ExportProvisioning").WithSummary("Export the provisioning document")
            .WithDescription("The current catalog, ordered by key, in the format that POST applies.")
            .RequirePermission(TahsilPermissions.ProvisioningRead);

        provisioning.MapPost(string.Empty, async (
            ProvisioningDocument document,
            [Description("Validates the document and reports what would change, without changing anything.")] bool? dryRun,
            ApplyProvisioningHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(document, dryRun ?? false, cancellationToken)).ToOk())
            .WithName("ApplyProvisioning").WithSummary("Apply a provisioning document")
            .WithDescription("Creates what is missing and updates what differs, in one transaction, and never deletes. Each item goes through the "
                + "same rules as the API, with the caller's permissions; an error names the item and nothing is applied.")
            .ProducesValidationProblem()
            .RequirePermission(TahsilPermissions.ProvisioningWrite);

        return api;
    }
}
