using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Invoices;
using Tahsil.Application.Webhooks;
using Tahsil.Infrastructure.Auditing;
using Tahsil.Infrastructure.Billing;
using Tahsil.Infrastructure.Clock;
using Tahsil.Infrastructure.Idempotency;
using Tahsil.Infrastructure.Outbox;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Providers;
using Tahsil.Infrastructure.Security;
using Tahsil.Infrastructure.Webhooks;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure;

public static class DependencyInjection
{
    private const string MigrationsHistoryTable = "__ef_migrations_history";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        AddPersistence(services);
        AddSecurity(services);
        AddBillingClock(services);
        AddProviders(services);
        AddBilling(services);
        AddWebhooks(services);
        AddWorkers(services);
        AddOutbox(services);
        AddAuditing(services);

        services.AddScoped<IdempotencyScope>();
        services.AddScoped<IIdempotency, IdempotencyRecorder>();
        services.AddSingleton<IdempotencyStore>();
        AddHostedSingleton<IdempotencyCleanup>(services);
        AddHostedSingleton<Portal.PortalSessionCleanup>(services);

        return services;
    }

    private static void AddPersistence(IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .Validate<IConfiguration>(
                (_, configuration) => !string.IsNullOrWhiteSpace(GetConnectionString(configuration)),
                $"The connection string 'ConnectionStrings:{DatabaseOptions.ConnectionStringName}' is not configured.")
            .ValidateOnStart();

        // The connection string is resolved when a context is created, so configuration
        // added late (integration tests, design-time tools) is honored.
        services.AddDbContext<TahsilDbContext>((serviceProvider, options) => options
            .UseNpgsql(
                GetConnectionString(serviceProvider.GetRequiredService<IConfiguration>()),
                npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, TahsilDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(serviceProvider.GetRequiredService<OutboxSignalInterceptor>()));

        services.AddScoped<ITahsilDbContext>(provider => provider.GetRequiredService<TahsilDbContext>());
        services.AddScoped<DatabasePreparation>();
    }

    private static void AddSecurity(IServiceCollection services)
    {
        services.AddOptions<SecurityOptions>()
            .BindConfiguration(SecurityOptions.SectionName)
            .Validate(
                options => SecurityOptions.IsValidMasterKey(options.MasterKey),
                $"{SecurityOptions.SectionName}:MasterKey must be a base64-encoded 256-bit key. Generate one with 'openssl rand -base64 32'.")
            .Validate(
                options => SecurityOptions.IsValidKeyId(options.MasterKeyId),
                $"{SecurityOptions.SectionName}:MasterKeyId must be between 1 and 255.")
            .Validate(
                options => options.HasValidPreviousKeys(),
                $"{SecurityOptions.SectionName}:PreviousMasterKeys must map IDs between 1 and 255, other than MasterKeyId, to base64-encoded 256-bit keys.")
            // Reads the mode from configuration: resolving the server options here would report their own
            // validation failures a second time.
            .Validate<IConfiguration>(
                (options, configuration) => ReadMode(configuration) != TahsilMode.Live || !options.IsDevelopmentMasterKey(),
                $"{SecurityOptions.SectionName}:MasterKey is the public development key, which Live mode refuses. Generate one with 'openssl rand -base64 32'.")
            .ValidateOnStart();

        services.AddSingleton<ISecretProtector, SecretProtector>();
        services.AddSingleton<ISecretEncryption, SecretEncryption>();
        services.AddSingleton<IEncryptedStore, DataProtectionKeyRingStore>();
        services.AddSingleton<IEncryptedStore>(new EncryptedColumn("customers", "tax_id", SecretPurposes.CustomerTaxId));
        services.AddSingleton<IEncryptedStore>(new EncryptedColumn("provider_connections", "credentials", SecretPurposes.ConnectionCredentials));
        services.AddSingleton<IEncryptedStore>(new EncryptedColumn("invoices", "customer_tax_id", SecretPurposes.CustomerTaxId));
        services.AddSingleton<IEncryptedStore>(new EncryptedColumn("payment_methods", "token", SecretPurposes.PaymentMethodToken));
        services.AddSingleton<IEncryptedStore>(new EncryptedColumn("provider_notifications", "raw_body", SecretPurposes.ProviderNotificationRawBody));
        services.AddSingleton<IEncryptedStore>(new EncryptedColumn("webhook_endpoints", "secret", SecretPurposes.WebhookEndpointSecret));
        services.AddSingleton<IEncryptedStore>(new EncryptedColumn("webhook_endpoints", "previous_secret", SecretPurposes.WebhookEndpointSecret));
        services.AddSingleton<IEncryptedStore>(new EncryptedColumn("checkout_sessions", "token", SecretPurposes.CheckoutToken));
        services.AddSingleton<IEncryptedStore>(new EncryptedColumn("invoice_link_keys", "key", SecretPurposes.InvoiceLinkKey, boundToRow: false));
        services.AddScoped<SecretReencryption>();

        // Every instance shares one key ring, stored in PostgreSQL and encrypted with the master key.
        services.AddDataProtection()
            .SetApplicationName("Tahsil")
            .PersistKeysToDbContext<TahsilDbContext>();
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>, ConfigureKeyRingEncryption>();
    }

    private static void AddProviders(IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddSingleton<IProviderContextFactory, ProviderContextFactory>();
    }

    private static void AddWebhooks(IServiceCollection services)
    {
        services.AddScoped<WebhookSender>();
        services.AddHttpClient(WebhookSender.HttpClientName)
            .ConfigureHttpClient((provider, client) => WebhookHttpClient.Configure(client, provider.GetRequiredService<IOptions<WebhookOptions>>().Value.Timeout))
            .ConfigurePrimaryHttpMessageHandler(provider => WebhookHttpClient.Handler(provider.GetRequiredService<IOptions<WebhookOptions>>().Value.AllowPrivateNetworks));
        AddHostedSingleton<WebhookDeliveryService>(services);
        AddHostedSingleton<WebhookDeliveryCleanup>(services);
    }

    private static void AddBilling(IServiceCollection services)
    {
        services.AddScoped<BillingLocks>();
        services.AddScoped<IBillingLocks>(provider => provider.GetRequiredService<BillingLocks>());
        services.AddScoped<IInvoiceNumbers>(provider => provider.GetRequiredService<BillingLocks>());
    }

    private static void AddBillingClock(IServiceCollection services)
    {
        services.AddSingleton<TestClockOffset>();
        services.AddSingleton<BillingClock, OffsetBillingClock>();
        services.AddSingleton<Application.TestClock.ITimeTravel, TimeTravel>();
        services.AddScoped<Application.AttentionItems.IWorkerAttention, WorkerAttention>();
        services.AddHostedService<TestClockRefresher>();
    }

    private static void AddWorkers(IServiceCollection services)
    {
        services.AddOptions<WorkerOptions>()
            .BindConfiguration(WorkerOptions.SectionName)
            .Validate(
                options => options.IsValid(),
                $"{WorkerOptions.SectionName}: the polling interval must be positive, the batch size between 1 and 1000, the lease at least 30 seconds and the payment concurrency between 1 and 64.")
            .ValidateOnStart();

        services.AddSingleton<WorkerSignals>();
        services.AddSingleton<IWorkerSignals>(provider => provider.GetRequiredService<WorkerSignals>());
    }

    private static void AddOutbox(IServiceCollection services)
    {
        services.AddOptions<OutboxOptions>()
            .BindConfiguration(OutboxOptions.SectionName)
            .Validate(options => options.IsValid(), $"{OutboxOptions.SectionName}: intervals must be positive and the batch size between 1 and 1000.")
            .ValidateOnStart();

        services.AddSingleton(new OutboxMessageTypes([typeof(Application.DependencyInjection).Assembly]));
        services.AddScoped<OutboxSignalInterceptor>();
        services.AddScoped<IOutbox, Outbox.Outbox>();
        AddHostedSingleton<OutboxDispatcher>(services);
        AddHostedSingleton<PaymentCollectionWorker>(services);
        AddHostedSingleton<ReconciliationWorker>(services);
        AddHostedSingleton<RefundReconciliationWorker>(services);
        AddHostedSingleton<ProviderNotificationWorker>(services);
        AddHostedSingleton<ProviderNotificationCleanup>(services);
        AddHostedSingleton<PaymentMethodExpiryWorker>(services);
        AddHostedSingleton<CheckoutExpiryWorker>(services);
        AddHostedSingleton<SubscriptionCycleWorker>(services);
        AddHostedSingleton<InvoiceFinalizationWorker>(services);
        AddHostedSingleton<DunningWorker>(services);
        AddHostedSingleton<InvoiceReminderWorker>(services);
        services.AddSingleton<Application.Checkout.ILegalDocumentRenderer, LegalDocuments.FluidLegalDocumentRenderer>();
        services.AddOptions<Email.EmailOptions>().BindConfiguration(Email.EmailOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<Application.Notifications.IEmailSender, Email.SmtpEmailSender>();
        services.AddSingleton<Application.Notifications.IEmailTemplateRenderer, Email.FluidEmailTemplateRenderer>();
        services.AddHostedService<Email.EmailStartupWarnings>();
        AddHostedSingleton<Email.OperatorDigestJob>(services);
        AddHostedSingleton<Email.TrialEndingReminderWorker>(services);
        AddHostedSingleton<Email.RenewalReminderWorker>(services);
        AddHostedSingleton<Email.CardExpiryReminderWorker>(services);
        AddHostedSingleton<OutboxCleanup>(services);
    }

    private static void AddAuditing(IServiceCollection services)
    {
        services.AddOptions<AuditOptions>()
            .BindConfiguration(AuditOptions.SectionName)
            .Validate(options => options.IsValid(), $"{AuditOptions.SectionName}:RetentionPeriod must be at least a day.")
            .ValidateOnStart();

        services.AddScoped<IAuditLog, AuditLog>();
        AddHostedSingleton<AuditRetention>(services);
    }

    /// <summary>Registers a background service that can also be resolved by its type, so tests can run it directly.</summary>
    private static void AddHostedSingleton<TService>(IServiceCollection services)
        where TService : class, IHostedService
    {
        services.AddSingleton<TService>();
        services.AddHostedService(provider => provider.GetRequiredService<TService>());
    }

    private static TahsilMode ReadMode(IConfiguration configuration) =>
        configuration.GetSection(ServerOptions.SectionName).GetValue<TahsilMode>(nameof(ServerOptions.Mode));

    private static string? GetConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);
}
