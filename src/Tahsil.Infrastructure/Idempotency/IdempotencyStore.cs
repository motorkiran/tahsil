using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Idempotency;

/// <summary>What to do with a request that carries an idempotency key.</summary>
internal abstract record IdempotencyDecision
{
    /// <summary>The key is new, or its earlier request died before creating anything: run the request.</summary>
    public sealed record Proceed(Guid RecordId) : IdempotencyDecision;

    /// <summary>The earlier request completed: answer with its response.</summary>
    public sealed record Replay(int Status, string? Headers, byte[] Body) : IdempotencyDecision;

    /// <summary>The earlier request died after creating its resource: answer with the resource as it is now.</summary>
    public sealed record Recover(Guid RecordId, string ResourceType, Guid ResourceId) : IdempotencyDecision;

    public sealed record Refuse(Domain.Common.Error Error) : IdempotencyDecision;
}

/// <summary>
/// The idempotency records, each change in its own short unit of work, apart from the request's: the record must be
/// visible to concurrent duplicates before the request runs, and survive the request's failure.
/// </summary>
internal sealed class IdempotencyStore(IServiceScopeFactory scopes, TimeProvider time)
{
    public async Task<IdempotencyDecision> BeginAsync(
        Guid apiKeyId, string key, string method, string path, byte[] requestHash, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();

        var record = new IdempotencyRecord
        {
            Id = Guid.CreateVersion7(now),
            ApiKeyId = apiKeyId,
            Key = key,
            Method = method,
            Path = path,
            RequestHash = requestHash,
            Status = IdempotencyStatus.InProgress,
            LeaseExpiresAt = now + IdempotencyRecord.Lease,
            CreatedAt = now,
            ExpiresAt = now + IdempotencyRecord.Retention,
        };
        context.IdempotencyRecords.Add(record);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new IdempotencyDecision.Proceed(record.Id);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            context.ChangeTracker.Clear();
        }

        var existing = await context.IdempotencyRecords.AsNoTracking()
            .SingleAsync(candidate => candidate.ApiKeyId == apiKeyId && candidate.Key == key, cancellationToken);

        if (!existing.RequestHash.AsSpan().SequenceEqual(requestHash))
        {
            return new IdempotencyDecision.Refuse(IdempotencyErrors.KeyReused);
        }

        if (existing.Status == IdempotencyStatus.Completed)
        {
            return new IdempotencyDecision.Replay(existing.ResponseStatus!.Value, existing.ResponseHeaders, existing.ResponseBody ?? []);
        }

        if (existing.LeaseExpiresAt > now)
        {
            return new IdempotencyDecision.Refuse(IdempotencyErrors.InProgress);
        }

        // The earlier request died. Take its lease over, unless another retry just did.
        var taken = await context.IdempotencyRecords
            .Where(candidate => candidate.Id == existing.Id && candidate.LeaseExpiresAt == existing.LeaseExpiresAt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.LeaseExpiresAt, now + IdempotencyRecord.Lease), cancellationToken);
        if (taken == 0)
        {
            return new IdempotencyDecision.Refuse(IdempotencyErrors.InProgress);
        }

        return existing is { ResourceType: { } type, ResourceId: { } id }
            ? new IdempotencyDecision.Recover(existing.Id, type, id)
            : new IdempotencyDecision.Proceed(existing.Id);
    }

    public async Task CompleteAsync(Guid recordId, int status, string headers, byte[] body, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().IdempotencyRecords
            .Where(record => record.Id == recordId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.Status, IdempotencyStatus.Completed)
                    .SetProperty(record => record.ResponseStatus, status)
                    .SetProperty(record => record.ResponseHeaders, headers)
                    .SetProperty(record => record.ResponseBody, body),
                cancellationToken);
    }

    /// <summary>Forgets a request that failed on the server's side, so the client can retry it.</summary>
    public async Task DeleteAsync(Guid recordId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().IdempotencyRecords
            .Where(record => record.Id == recordId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>The idempotency record of the current request, if it has one.</summary>
internal sealed class IdempotencyScope
{
    public Guid? RecordId { get; set; }
}

/// <summary>Records the created resource on the request's idempotency record, in the handler's own save.</summary>
internal sealed class IdempotencyRecorder(TahsilDbContext context, IdempotencyScope scope) : IIdempotency
{
    public void RecordCreated(string resourceType, Guid id)
    {
        if (scope.RecordId is not { } recordId)
        {
            return;
        }

        var record = new IdempotencyRecord { Id = recordId };
        var entry = context.IdempotencyRecords.Attach(record);
        record.ResourceType = resourceType;
        record.ResourceId = id;
        entry.Property(candidate => candidate.ResourceType).IsModified = true;
        entry.Property(candidate => candidate.ResourceId).IsModified = true;
    }
}
