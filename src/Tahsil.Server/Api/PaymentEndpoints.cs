using System.ComponentModel;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Payments, and paying an invoice with a saved card (PAY §10, INV §5.3).</summary>
internal static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("invoices/{id:guid}/pay", async (Guid id, PayInvoiceRequest request, PayInvoiceHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, request, cancellationToken)).ToCreated(payment => Location(payment.Id)))
            .WithTags("Invoices").WithName("PayInvoice").WithSummary("Pay an invoice with a saved card")
            .WithDescription("Charges one of the customer's saved cards now, off session, whatever the invoice's schedule. The payment's status says how it ended; `processing` means the provider's answer is still being confirmed.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.InvoicesWrite);

        var payments = api.MapGroup("payments").WithTags("Payments");
        payments.MapGet(string.Empty, async (
            [Description("Only this customer's payments.")] Guid? customer,
            [Description("Only payments of this invoice.")] Guid? invoice,
            [Description("`processing`, `requires_action`, `succeeded`, `failed` or `canceled`.")] PaymentStatus? status,
            [Description("Only payments through this provider connection.")] Guid? connection,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListPaymentsHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(new ListPaymentsQuery(customer, invoice, status, connection, cursor, limit), cancellationToken)).ToOk())
            .WithName("ListPayments").WithSummary("List payments").WithDescription("Newest first.").RequirePermission(TahsilPermissions.PaymentsRead);

        payments.MapGet("{id:guid}", async (Guid id, GetPaymentHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithName("GetPayment").WithSummary("Get a payment").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.PaymentsRead);
        return api;
    }

    public static IServiceCollection AddPaymentResourceReaders(this IServiceCollection services) =>
        services.AddSingleton(new IdempotentResourceReader(ResourceTypes.Payment, async (http, id) =>
            (await http.RequestServices.GetRequiredService<GetPaymentHandler>().HandleAsync(id, http.RequestAborted)).ToCreated(payment => Location(payment.Id))));

    private static string Location(Guid id) => $"{TahsilApi.BasePath}/payments/{id}";
}
