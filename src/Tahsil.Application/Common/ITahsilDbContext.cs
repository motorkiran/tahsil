using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Tahsil.Domain.ApiKeys;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Common;

/// <summary>The unit of work for use cases. EF Core's <c>DbSet</c> already is the repository.</summary>
public interface ITahsilDbContext
{
    DbSet<ApiKey> ApiKeys { get; }

    DbSet<TaxRate> TaxRates { get; }

    DbSet<Meter> Meters { get; }

    DbSet<Product> Products { get; }

    DbSet<Price> Prices { get; }

    DbSet<Customer> Customers { get; }

    DbSet<ProviderConnection> ProviderConnections { get; }

    DbSet<ConnectionCurrency> ConnectionCurrencies { get; }

    DbSet<ProviderCustomer> ProviderCustomers { get; }

    DbSet<CustomerBalance> CustomerBalances { get; }

    DbSet<BalanceTransaction> BalanceTransactions { get; }

    DbSet<Invoice> Invoices { get; }

    DbSet<PendingInvoiceItem> PendingInvoiceItems { get; }

    DbSet<Payment> Payments { get; }

    DbSet<PaymentMethod> PaymentMethods { get; }

    DbSet<ConsentRecord> ConsentRecords { get; }

    DbSet<AttentionItem> AttentionItems { get; }

    /// <summary>The audit trail, for queries. Record events through the audit log.</summary>
    IQueryable<AuditEvent> AuditEvents { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets every loaded and changed entity, so a retried use case reads current data.</summary>
    void DiscardChanges();

    /// <summary>Starts a transaction, or joins the one already in progress, which then decides the outcome.</summary>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}
