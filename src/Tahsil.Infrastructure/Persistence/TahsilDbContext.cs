using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Tahsil.Application.Common;
using Tahsil.Domain.ApiKeys;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Clock;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;
using Tahsil.Domain.Webhooks;
using Tahsil.Infrastructure.Idempotency;
using Tahsil.Infrastructure.Outbox;
using Tahsil.Infrastructure.Persistence.Conventions;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Persistence;

public sealed class TahsilDbContext(DbContextOptions<TahsilDbContext> options)
    : DbContext(options), ITahsilDbContext, IDataProtectionKeyContext
{
    public const string Schema = "tahsil";

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<TaxRate> TaxRates => Set<TaxRate>();

    public DbSet<Meter> Meters => Set<Meter>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Price> Prices => Set<Price>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<ProviderConnection> ProviderConnections => Set<ProviderConnection>();

    public DbSet<Domain.Checkout.CheckoutSession> CheckoutSessions => Set<Domain.Checkout.CheckoutSession>();

    public DbSet<Domain.Checkout.LegalDocumentVersion> LegalDocumentVersions => Set<Domain.Checkout.LegalDocumentVersion>();

    public DbSet<Domain.Notifications.NotificationLog> NotificationLogs => Set<Domain.Notifications.NotificationLog>();

    public DbSet<ConnectionCurrency> ConnectionCurrencies => Set<ConnectionCurrency>();

    public DbSet<ProviderCustomer> ProviderCustomers => Set<ProviderCustomer>();

    public DbSet<CustomerBalance> CustomerBalances => Set<CustomerBalance>();

    public DbSet<BalanceTransaction> BalanceTransactions => Set<BalanceTransaction>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<PendingInvoiceItem> PendingInvoiceItems => Set<PendingInvoiceItem>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<ProviderNotification> ProviderNotifications => Set<ProviderNotification>();

    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();

    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();

    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();

    public DbSet<AttentionItem> AttentionItems => Set<AttentionItem>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    IQueryable<AuditEvent> ITahsilDbContext.AuditEvents => AuditEvents;

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<TestClock> TestClocks => Set<TestClock>();

    public DbSet<WorkerFailure> WorkerFailures => Set<WorkerFailure>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// Starts a transaction, or joins the one in progress: a use case that runs as part of a larger one, such as
    /// applying a provisioning file, then commits or rolls back with it.
    /// </summary>
    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        Database.CurrentTransaction is { } current ? new JoinedTransaction(current) : await Database.BeginTransactionAsync(cancellationToken);

    public void DiscardChanges() => ChangeTracker.Clear();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        PersistenceConventions.Configure(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TahsilDbContext).Assembly);
        PersistenceConventions.Apply(modelBuilder);
    }
}
