using System.ComponentModel;
using Microsoft.AspNetCore.Http.HttpResults;
using Tahsil.Application.Common;
using Tahsil.Application.Customers;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Customers, addressed by Tahsil's ID or by the application's own external ID.</summary>
internal static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder api)
    {
        var customers = api.MapGroup("customers").WithTags("Customers");

        customers.MapGet(string.Empty, ListAsync).WithName("ListCustomers").WithSummary("List and search customers")
            .WithDescription("Newest first. `email` and `externalId` match exactly (the email ignoring case); `q` matches the beginning of the email, name or external ID.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequirePermission(TahsilPermissions.CustomersRead);

        customers.MapPost(string.Empty, async (CreateCustomerRequest request, CreateCustomerHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, customer => Location(customer.Id)))
            .WithName("CreateCustomer").WithSummary("Create a customer")
            .WithDescription("An external ID that another customer has answers 409 customer.external_id_taken with that customer's ID in `customerId`.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CustomersWrite);

        customers.MapGet("{id:guid}", async (Guid id, GetCustomerHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk(response))
            .WithName("GetCustomer").WithSummary("Get a customer").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CustomersRead);

        customers.MapPatch("{id:guid}", async (Guid id, UpdateCustomerRequest request, UpdateCustomerHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(id, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdateCustomer").WithSummary("Update a customer")
            .WithDescription("JSON Merge Patch: omitted fields stay, null clears.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.CustomersWrite);

        customers.MapDelete("{id:guid}", DeleteAsync).WithName("DeleteCustomer").WithSummary("Delete or anonymize a customer")
            .WithDescription("Deletes a customer without billing history (204). A customer with history is anonymized instead, and returned (200).")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CustomersWrite);

        customers.MapGet("{id:guid}/export", async (Guid id, ExportCustomerHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk())
            .WithName("ExportCustomer").WithSummary("Export a customer's data")
            .WithDescription("Everything Tahsil holds about the customer, for data subject requests under KVKK.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CustomersRead);

        customers.MapGet("by-external-id/{externalId}", async (string externalId, GetCustomerHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleByExternalIdAsync(externalId, cancellationToken)).ToOk(response))
            .WithName("GetCustomerByExternalId").WithSummary("Get a customer by external ID").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CustomersRead);

        customers.MapPut("by-external-id/{externalId}", UpsertAsync).WithName("UpsertCustomerByExternalId").WithSummary("Create or update a customer by external ID")
            .WithDescription("Creates the customer (201) or updates it as a JSON Merge Patch (200), for applications that sync customers on every sign-in.")
            .ProducesValidationProblem()
            .RequirePermission(TahsilPermissions.CustomersWrite);

        return api;
    }

    public static IServiceCollection AddCustomerResourceReaders(this IServiceCollection services) =>
        services.AddSingleton(new IdempotentResourceReader(ResourceTypes.Customer, async (http, id) =>
            (await http.RequestServices.GetRequiredService<GetCustomerHandler>().HandleAsync(id, http.RequestAborted))
            .ToCreated(http.Response, customer => Location(customer.Id))));

    private static async Task<Results<Ok<Page<CustomerResponse>>, ProblemHttpResult>> ListAsync(
        [Description("Matches the beginning of the email, name or external ID, ignoring case.")] string? q,
        [Description("An email address, ignoring case.")] string? email,
        [Description("The application's ID for the customer.")] string? externalId,
        [Description("The `nextCursor` of the previous page.")] string? cursor,
        [Description("Page size, 50 by default and at most 200.")] int? limit,
        ListCustomersHandler handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ListCustomersQuery(q, email, externalId, cursor, limit), cancellationToken)).ToOk();

    private static async Task<Results<NoContent, Ok<CustomerResponse>, ProblemHttpResult>> DeleteAsync(
        Guid id, DeleteCustomerHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error);
        }

        return result.Value is { } anonymized ? TypedResults.Ok(anonymized) : TypedResults.NoContent();
    }

    private static async Task<Results<Created<CustomerResponse>, Ok<CustomerResponse>, ProblemHttpResult>> UpsertAsync(
        string externalId, UpdateCustomerRequest request, UpsertCustomerHandler handler, HttpResponse response, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(externalId, request, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error);
        }

        var (customer, created) = result.Value;
        response.Headers.ETag = ETags.Format(customer.Version);
        return created ? TypedResults.Created(Location(customer.Value.Id), customer.Value) : TypedResults.Ok(customer.Value);
    }

    private static string Location(Guid id) => $"{TahsilApi.BasePath}/customers/{id}";
}
