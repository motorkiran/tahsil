using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Providers.Abstractions;
using Model = Tahsil.Domain.Common;

namespace Tahsil.Application.AttentionItems;

/// <summary>Worker items that need an operator (ENG-6), as the infrastructure records them.</summary>
public interface IWorkerAttention
{
    Task<IReadOnlyList<WorkerAttentionItem>> ListAsync(CancellationToken cancellationToken);
}

public sealed record WorkerAttentionItem(string Worker, Guid ItemId, int FailureCount, DateTimeOffset FirstFailedAt, DateTimeOffset LastFailedAt, string? LastError);

public static class AttentionErrors
{
    public static readonly Error NotFound = Error.NotFound("attention.not_found", "The attention item does not exist.");
    public static readonly Error InvalidNote = Error.Validation("attention.invalid", "Say how it was resolved, in at most 500 characters.", "note");
    public static readonly Error PaymentNotInFlight = Error.Conflict(
        "attention.payment_settled", "The payment is no longer waiting for an outcome; nothing is left to resolve.");
}

/// <summary>What operators must look at: open items first, and worker items that keep failing.</summary>
public sealed class ListAttentionItemsHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<Page<AttentionItemResponse>>> HandleAsync(bool includeResolved, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.PaymentsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var items = context.AttentionItems.AsNoTracking();
        if (!includeResolved)
        {
            items = items.Where(item => item.ResolvedAt == null);
        }

        var list = await items.OrderByDescending(item => item.RaisedAt).Take(Paging.MaxLimit).ToListAsync(cancellationToken);
        return new Page<AttentionItemResponse>([.. list.Select(AttentionMapper.ToResponse)], null);
    }
}

public sealed class ListWorkerAttentionHandler(IAuthorizer authorizer, IWorkerAttention workers)
{
    public async Task<Result<Page<WorkerAttentionResponse>>> HandleAsync(CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.PaymentsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var items = await workers.ListAsync(cancellationToken);
        return new Page<WorkerAttentionResponse>(
            [.. items.Select(item => new WorkerAttentionResponse(item.Worker, item.ItemId, item.FailureCount, item.FirstFailedAt, item.LastFailedAt, item.LastError))], null);
    }
}

/// <summary>Closes an attention item with what the operator did about it.</summary>
public sealed class ResolveAttentionItemHandler(ITahsilDbContext context, IAuthorizer authorizer, IRequestContext request, IAuditLog audit, BillingClock clock)
{
    public async Task<Result<AttentionItemResponse>> HandleAsync(Guid id, string resolution, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Text.FitsRequired(resolution, Model.AttentionItem.ResolutionMaxLength))
        {
            return AttentionErrors.InvalidNote;
        }

        if (await context.AttentionItems.SingleOrDefaultAsync(item => item.Id == id, cancellationToken) is not { } item)
        {
            return AttentionErrors.NotFound;
        }

        if (item.IsOpen)
        {
            item.Resolve(resolution.Trim(), request.Caller.ToActor(), clock.GetUtcNow());
            audit.Record(AuditActions.AttentionResolved, new AuditSubject(item.SubjectType, item.SubjectId.ToString()), new Dictionary<string, object?>
            {
                ["kind"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(item.Kind.ToString()),
                ["resolution"] = item.Resolution,
            }, customerId: item.CustomerId);
            await context.SaveChangesAsync(cancellationToken);
        }

        return AttentionMapper.ToResponse(item);
    }
}

/// <summary>
/// Settles a payment reconciliation could not settle, with the provider's evidence (ENG-16): it is applied like the
/// provider's own answer, audited with the operator, and its open attention items are resolved.
/// </summary>
public sealed class ResolvePaymentHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IRequestContext request, IAuditLog audit, PaymentOutcomes outcomes, BillingClock clock)
{
    public async Task<Result<PaymentResponse>> HandleAsync(Guid paymentId, ResolvePaymentRequest resolution, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Text.FitsRequired(resolution.Note, Model.AttentionItem.ResolutionMaxLength))
        {
            return AttentionErrors.InvalidNote;
        }

        if (await context.Payments.AsNoTracking().SingleOrDefaultAsync(payment => payment.Id == paymentId, cancellationToken) is not { } current)
        {
            return PaymentErrors.NotFound;
        }

        if (!current.IsInFlight)
        {
            return AttentionErrors.PaymentNotInFlight;
        }

        var outcome = resolution.Succeeded
            ? new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Succeeded, ProviderPaymentId = Text.Normalize(resolution.ProviderPaymentId) }
            : PaymentOutcome.Failure(FailureCode.ProviderError, providerMessage: resolution.Note);
        var payment = await outcomes.ApplyAsync(paymentId, outcome, cancellationToken);

        var actor = request.Caller.ToActor();
        var now = clock.GetUtcNow();
        foreach (var item in await context.AttentionItems.Where(item => item.SubjectId == paymentId && item.ResolvedAt == null).ToListAsync(cancellationToken))
        {
            item.Resolve(resolution.Note.Trim(), actor, now);
        }

        audit.Record(AuditActions.PaymentResolved, AuditSubject.Of(AuditSubjects.Payment, paymentId), new Dictionary<string, object?>
        {
            ["succeeded"] = resolution.Succeeded,
            ["providerPaymentId"] = Text.Normalize(resolution.ProviderPaymentId),
            ["note"] = resolution.Note.Trim(),
        }, customerId: payment.CustomerId);
        await context.SaveChangesAsync(cancellationToken);
        return PaymentMapper.ToResponse(payment);
    }
}

internal static class AttentionMapper
{
    public static AttentionItemResponse ToResponse(Model.AttentionItem item) => new(
        item.Id,
        JsonNamingPolicy.SnakeCaseLower.ConvertName(item.Kind.ToString()),
        item.SubjectType,
        item.SubjectId,
        item.CustomerId,
        item.Details,
        item.RaisedAt,
        item.ResolvedAt,
        item.Resolution,
        item.ResolvedBy?.ToResponse());
}
