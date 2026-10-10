using Microsoft.EntityFrameworkCore;
using Tahsil.Application.AttentionItems;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Workers;

internal sealed class WorkerAttention(TahsilDbContext context) : IWorkerAttention
{
    public async Task<IReadOnlyList<WorkerAttentionItem>> ListAsync(CancellationToken cancellationToken) =>
        await context.WorkerFailures.AsNoTracking()
            .Where(failure => failure.AttentionRaisedAt != null)
            .OrderByDescending(failure => failure.LastFailedAt)
            .Select(failure => new WorkerAttentionItem(failure.Worker, failure.ItemId, failure.FailureCount, failure.FirstFailedAt, failure.LastFailedAt, failure.LastError))
            .ToListAsync(cancellationToken);
}
