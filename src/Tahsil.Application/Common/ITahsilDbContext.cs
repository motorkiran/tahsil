using Microsoft.EntityFrameworkCore.Storage;
using Tahsil.Domain.Auditing;

namespace Tahsil.Application.Common;

/// <summary>The unit of work for use cases. EF Core's <c>DbSet</c> already is the repository.</summary>
public interface ITahsilDbContext
{
    /// <summary>The audit trail, for queries. Record events through the audit log.</summary>
    IQueryable<AuditEvent> AuditEvents { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a transaction, or joins the one already in progress, which then decides the outcome.</summary>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}
