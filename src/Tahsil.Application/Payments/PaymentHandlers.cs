using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Common;
using Model = Tahsil.Domain.Payments;

namespace Tahsil.Application.Payments;

/// <summary>Charges a saved card of the customer for an invoice now, off session (INV §5.3).</summary>
/// <summary>Pays an invoice now (INV §5.3): a saved card charged off session, or money received outside Tahsil recorded (INV-14).</summary>
public sealed class PayInvoiceHandler(IAuthorizer authorizer, MerchantPayments payments, OutOfBandPayments outOfBand)
{
    public async Task<Result<PaymentResponse>> HandleAsync(Guid invoiceId, PayInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var paid = (request.PaymentMethod, request.OutOfBand) switch
        {
            ({ } method, null) => await payments.ChargeAsync(invoiceId, method, cancellationToken),
            (null, { } received) => await outOfBand.RecordAsync(invoiceId, received, cancellationToken),
            _ => Error.Validation("payment.invalid", "Give either a saved card as paymentMethod or the money received as outOfBand.", "paymentMethod"),
        };
        return paid.IsFailure ? paid.Error : PaymentMapper.ToResponse(paid.Value);
    }
}

public sealed class GetPaymentHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<PaymentResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.PaymentsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.Payments.AsNoTracking().SingleOrDefaultAsync(payment => payment.Id == id, cancellationToken) is { } payment
            ? PaymentMapper.ToResponse(payment)
            : PaymentErrors.NotFound;
    }
}

public sealed record ListPaymentsQuery(
    Guid? Customer = null, Guid? Invoice = null, PaymentStatus? Status = null, Guid? Connection = null, string? Cursor = null, int? Limit = null, string? FailureCode = null);

public sealed class ListPaymentsHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<Model.Payment, Guid> Newest = new("newest", payment => payment.Id, payment => payment.Id, Descending: true);

    public async Task<Result<Page<PaymentResponse>>> HandleAsync(ListPaymentsQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.PaymentsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Paging.IsValidLimit(query.Limit ?? Paging.DefaultLimit))
        {
            return CommonErrors.InvalidLimit();
        }

        var payments = context.Payments.AsNoTracking();
        if (query.Customer is { } customer)
        {
            payments = payments.Where(payment => payment.CustomerId == customer);
        }

        if (query.Invoice is { } invoice)
        {
            payments = payments.Where(payment => payment.InvoiceId == invoice);
        }

        if (query.Status is { } status)
        {
            var modelStatus = status.Map<Model.PaymentStatus>();
            payments = payments.Where(payment => payment.Status == modelStatus);
        }

        if (query.Connection is { } connection)
        {
            payments = payments.Where(payment => payment.ConnectionId == connection);
        }

        if (query.FailureCode is { } failureCode)
        {
            payments = payments.Where(payment => payment.FailureCode == failureCode);
        }

        var page = await Newest.ReadAsync(payments, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<PaymentResponse>([.. page.Items.Select(PaymentMapper.ToResponse)], page.NextCursor);
    }
}

public static class PaymentMapper
{
    public static PaymentResponse ToResponse(Model.Payment payment) => new()
    {
        Id = payment.Id,
        Reference = payment.Reference,
        InvoiceId = payment.InvoiceId,
        CustomerId = payment.CustomerId,
        ConnectionId = payment.ConnectionId,
        PaymentMethodId = payment.PaymentMethodId,
        Amount = payment.Amount,
        Currency = payment.Currency,
        Purpose = payment.Purpose.Map<PaymentPurpose>(),
        Status = payment.Status.Map<PaymentStatus>(),
        Initiator = payment.Initiator.Map<PaymentInitiator>(),
        Method = payment.Method.Map<PaymentMethodKind>(),
        OutOfBand = payment.OutOfBand is { } received ? new OutOfBandResponse(received.ReceivedAt, received.Reference, received.Note) : null,
        ProviderPaymentId = payment.ProviderPaymentId,
        ThreeDSecure = payment.ThreeDSecure,
        FailureCode = payment.FailureCode,
        FailureCategory = payment.FailureCategory,
        FailureMessage = payment.FailureMessage,
        ProviderFailureCode = payment.ProviderFailureCode,
        ProviderFailureMessage = payment.ProviderFailureMessage,
        AmountRefunded = payment.AmountRefunded,
        ExpiresAt = payment.ExpiresAt,
        SucceededAt = payment.SucceededAt,
        FailedAt = payment.FailedAt,
        CanceledAt = payment.CanceledAt,
        Actor = payment.Actor.ToResponse(),
        CreatedAt = payment.CreatedAt,
        UpdatedAt = payment.UpdatedAt,
    };
}

/// <summary>A connection that took payments or saved cards is disabled, never deleted (DOM-9).</summary>
public sealed class PaymentConnectionUsage(ITahsilDbContext context) : Connections.IConnectionUsage
{
    public async Task<bool> IsUsedAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await context.Payments.AnyAsync(payment => payment.ConnectionId == connectionId, cancellationToken)
        || await context.PaymentMethods.AnyAsync(method => method.ConnectionId == connectionId, cancellationToken);
}
