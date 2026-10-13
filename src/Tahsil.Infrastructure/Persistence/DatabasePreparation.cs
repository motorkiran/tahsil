using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Connections;
using Tahsil.Application.Provisioning;

namespace Tahsil.Infrastructure.Persistence;

/// <summary>
/// Brings the database up to date: migrations, the connections' mode rule and the Test mode simulator, the legal
/// documents' versions, then the provisioning file. EF Core holds a database lock while migrating, and an advisory lock covers the rest, so instances
/// that start at the same time take turns. Any failure stops startup: it means the configuration is wrong.
/// </summary>
internal sealed partial class DatabasePreparation(
    TahsilDbContext context,
    ConnectionStartup connections,
    LegalDocumentVersions legalDocuments,
    Application.Invoices.InvoiceLinks invoiceLinks,
    StartupProvisioning provisioning,
    IOptions<ProvisioningOptions> provisioningOptions,
    ILogger<DatabasePreparation> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await context.Database.MigrateAsync(cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKeys.DatabasePreparation})", cancellationToken);

        await connections.CheckModeAsync(cancellationToken);
        await connections.WarnOfDirectFormsAsync(cancellationToken);
        await connections.EnsureSimulatorAsync(cancellationToken);
        await legalDocuments.RegisterAsync(cancellationToken);
        await invoiceLinks.EnsureKeyAsync(context, cancellationToken);

        if (provisioningOptions.Value.FilePath is { Length: > 0 } path)
        {
            await ApplyProvisioningFileAsync(path, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ApplyProvisioningFileAsync(string path, CancellationToken cancellationToken) =>
        LogProvisioningApplied(logger, path, await provisioning.ApplyAsync(path, cancellationToken));

    [LoggerMessage(LogLevel.Information, "Applied the provisioning file {Path}: {Summary}")]
    private static partial void LogProvisioningApplied(ILogger logger, string path, string summary);
}
