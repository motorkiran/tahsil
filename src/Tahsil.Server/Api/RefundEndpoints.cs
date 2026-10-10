using System.ComponentModel;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Refunds of payments (PAY-18 to PAY-22).</summary>
internal static class RefundEndpoints
{
    public static IEndpointRouteBuilder MapRefundEndpoints(this IEndpointRouteBuilder api)
    {
        var refunds = api.MapGroup("refunds").WithTags("Refunds");
        refunds.MapPost(string.Empty, async (CreateRefundRequest request, CreateRefundHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(refund => Location(refund.Id)))
            .WithName("CreateRefund").WithSummary("Refund a payment")
            .WithDescription("Refunds what is left of a succeeded payment, or part of it. The provider may answer later: `processing` refunds settle by themselves. Refunds never change the status of the payment or its invoice.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.RefundsWrite);

        refunds.MapGet(string.Empty, async (
            [Description("Only refunds of this payment.")] Guid? payment,
            [Description("`processing`, `succeeded` or `failed`.")] string? status,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListRefundsHandler handler,
            CancellationToken cancellationToken) =>
            QueryValues.TryParseEnum<RefundStatus>(status, out var statusFilter)
                ? (await handler.HandleAsync(new ListRefundsQuery(payment, statusFilter, cursor, limit), cancellationToken)).ToOk()
                : ApiResults.Problem(CommonErrors.InvalidParameter(nameof(status))))
            .WithName("ListRefunds").WithSummary("List refunds").WithDescription("Newest first.").RequirePermission(TahsilPermissions.PaymentsRead);

        refunds.MapGet("{id:guid}", async (Guid id, GetRefundHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithName("GetRefund").WithSummary("Get a refund").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.PaymentsRead);
        return api;
    }

    public static IServiceCollection AddRefundResourceReaders(this IServiceCollection services) =>
        services.AddSingleton(new IdempotentResourceReader(ResourceTypes.Refund, async (http, id) =>
            (await http.RequestServices.GetRequiredService<GetRefundHandler>().HandleAsync(id, http.RequestAborted)).ToCreated(refund => Location(refund.Id))));

    private static string Location(Guid id) => $"{TahsilApi.BasePath}/refunds/{id}";
}
