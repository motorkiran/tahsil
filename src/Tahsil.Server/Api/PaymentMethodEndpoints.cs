using System.ComponentModel;
using Tahsil.Application.Common;
using Tahsil.Application.PaymentMethods;
using Tahsil.Application.TestClock;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Saved cards (CUS §2.3), and the simulator's test API (SIM-7, SIM-8).</summary>
internal static class PaymentMethodEndpoints
{
    public static IEndpointRouteBuilder MapPaymentMethodEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("customers/{id:guid}/payment-methods", async (
            Guid id,
            [Description("`active`, `expired` or `removed`.")] string? status,
            ListPaymentMethodsHandler handler,
            CancellationToken cancellationToken) =>
            QueryValues.TryParseEnum<PaymentMethodStatus>(status, out var statusFilter)
                ? (await handler.HandleAsync(id, statusFilter, cancellationToken)).ToOk()
                : ApiResults.Problem(CommonErrors.InvalidParameter(nameof(status))))
            .WithTags("Customers").WithName("ListPaymentMethods").WithSummary("List a customer's saved cards").WithDescription("Active cards first.")
            .ProducesProblem(StatusCodes.Status404NotFound).RequirePermission(TahsilPermissions.CustomersRead);

        api.MapPost("customers/{id:guid}/default-payment-method", async (Guid id, SetDefaultPaymentMethodRequest request, SetDefaultPaymentMethodHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, request.PaymentMethod, cancellationToken)).ToOk())
            .WithTags("Customers").WithName("SetDefaultPaymentMethod").WithSummary("Set a customer's default card")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CustomersWrite);

        api.MapGet("payment-methods/{id:guid}", async (Guid id, GetPaymentMethodHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithTags("Customers").WithName("GetPaymentMethod").WithSummary("Get a saved card").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CustomersRead);

        api.MapDelete("payment-methods/{id:guid}", async (Guid id, RemovePaymentMethodHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToNoContent())
            .WithTags("Customers").WithName("RemovePaymentMethod").WithSummary("Remove a saved card")
            .WithDescription("The card is never charged again and stops being the default; the provider is asked to delete its token.")
            .ProducesProblem(StatusCodes.Status404NotFound).RequirePermission(TahsilPermissions.CustomersWrite);
        return api;
    }

    /// <summary>The test API, mapped in Test mode only.</summary>
    public static IEndpointRouteBuilder MapSimulatorTestEndpoints(this IEndpointRouteBuilder api)
    {
        var clock = api.MapGroup("test/clock").WithTags("Test mode");
        clock.MapGet(string.Empty, async (GetTestClockHandler handler, CancellationToken cancellationToken) => (await handler.HandleAsync(cancellationToken)).ToOk())
            .WithName("GetTestClock").WithSummary("Read the billing clock").RequirePermission(TahsilPermissions.TestWrite);
        clock.MapPost(string.Empty, async (AdvanceClockRequest request, AdvanceTestClockHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToOk())
            .WithName("AdvanceTestClock").WithSummary("Travel forward in time")
            .WithDescription("Test mode only: moves the billing clock forward by `advanceBy` (an ISO 8601 duration such as `P1M`) or to `advanceTo`, at most 400 days, and processes everything that falls due on the way, at its own instant. Returns when done.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict).RequirePermission(TahsilPermissions.TestWrite);

        var simulator = api.MapGroup("test/simulator").WithTags("Test mode");
        simulator.MapPost("payment-methods", async (CreateSimulatorPaymentMethodRequest request, CreateTestPaymentMethodHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(method => $"{TahsilApi.BasePath}/payment-methods/{method.Id}"))
            .WithName("CreateSimulatorPaymentMethod").WithSummary("Save a simulator test card for a customer")
            .WithDescription("Test mode only: a saved card without a browser, with a consent record marked as made by the test API.")
            .ProducesValidationProblem().RequirePermission(TahsilPermissions.TestWrite);

        simulator.MapPatch("payment-methods/{id:guid}", async (Guid id, UpdateSimulatorPaymentMethodRequest request, UpdateTestPaymentMethodHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, request, cancellationToken)).ToOk())
            .WithName("UpdateSimulatorPaymentMethod").WithSummary("Change how a simulator card answers")
            .WithDescription("Test mode only: `succeed`, a failure code such as `insufficient_funds`, `timeout_after_charge`, `timeout_before_charge` or `pending`.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).RequirePermission(TahsilPermissions.TestWrite);
        return api;
    }
}
