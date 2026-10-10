using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Common;
using Model = Tahsil.Domain.Payments;

namespace Tahsil.Application.Payments;

public sealed class CreateRefundHandler(IAuthorizer authorizer, Refunds refunds)
{
    public async Task<Result<RefundResponse>> HandleAsync(CreateRefundRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.RefundsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var refund = await refunds.CreateAsync(request.Payment, request.Amount, request.Reason.Map<Model.RefundReason>(), request.Note, cancellationToken);
        return refund.IsFailure ? refund.Error : RefundMapper.ToResponse(refund.Value);
    }
}

public sealed class GetRefundHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<RefundResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.PaymentsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.Refunds.AsNoTracking().SingleOrDefaultAsync(refund => refund.Id == id, cancellationToken) is { } refund
            ? RefundMapper.ToResponse(refund)
            : RefundErrors.NotFound;
    }
}

public sealed record ListRefundsQuery(Guid? Payment = null, RefundStatus? Status = null, string? Cursor = null, int? Limit = null);

public sealed class ListRefundsHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<Model.Refund, Guid> Newest = new("newest", refund => refund.Id, refund => refund.Id, Descending: true);

    public async Task<Result<Page<RefundResponse>>> HandleAsync(ListRefundsQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.PaymentsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Paging.IsValidLimit(query.Limit ?? Paging.DefaultLimit))
        {
            return CommonErrors.InvalidLimit();
        }

        var refunds = context.Refunds.AsNoTracking();
        if (query.Payment is { } payment)
        {
            refunds = refunds.Where(refund => refund.PaymentId == payment);
        }

        if (query.Status is { } status)
        {
            var modelStatus = status.Map<Model.RefundStatus>();
            refunds = refunds.Where(refund => refund.Status == modelStatus);
        }

        var page = await Newest.ReadAsync(refunds, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<RefundResponse>([.. page.Items.Select(RefundMapper.ToResponse)], page.NextCursor);
    }
}

public static class RefundMapper
{
    public static RefundResponse ToResponse(Model.Refund refund) => new(
        refund.Id,
        refund.Reference,
        refund.PaymentId,
        refund.Amount,
        refund.Currency,
        refund.Reason.Map<RefundReason>(),
        refund.Kind.Map<RefundKind>(),
        refund.Status.Map<RefundStatus>(),
        refund.ProviderRefundId,
        refund.FailureCode,
        refund.FailureMessage,
        refund.Note,
        refund.Actor.ToResponse(),
        refund.SucceededAt,
        refund.FailedAt,
        refund.CreatedAt,
        refund.UpdatedAt);
}
