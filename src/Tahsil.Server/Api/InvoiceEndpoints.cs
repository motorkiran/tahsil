using System.ComponentModel;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Invoices, pending invoice items and customer balances (INV §8, CUS-17 to CUS-20).</summary>
internal static class InvoiceEndpoints
{
    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder api)
    {
        MapInvoices(api.MapGroup("invoices").WithTags("Invoices"));
        MapInvoiceItems(api.MapGroup("invoice-items").WithTags("Invoices"));
        MapBalances(api.MapGroup("customers/{customerId:guid}/balance-transactions").WithTags("Customers"));
        return api;
    }

    public static IServiceCollection AddInvoiceResourceReaders(this IServiceCollection services) => services
        .AddSingleton(new IdempotentResourceReader(ResourceTypes.Invoice, async (http, id) =>
            (await http.RequestServices.GetRequiredService<GetInvoiceHandler>().HandleAsync(id, http.RequestAborted))
            .ToCreated(http.Response, invoice => Location("invoices", invoice.Id))))
        .AddSingleton(new IdempotentResourceReader(ResourceTypes.InvoiceItem, async (http, id) =>
            (await http.RequestServices.GetRequiredService<GetInvoiceItemHandler>().HandleAsync(id, http.RequestAborted))
            .ToCreated(http.Response, item => Location("invoice-items", item.Id))))
        .AddSingleton(new IdempotentResourceReader(ResourceTypes.BalanceTransaction, async (http, id) =>
        {
            var customerId = Guid.Parse((string)http.Request.RouteValues["customerId"]!);
            return (await http.RequestServices.GetRequiredService<GetBalanceTransactionHandler>().HandleAsync(customerId, id, http.RequestAborted))
                .ToCreated(transaction => $"{TahsilApi.BasePath}/customers/{customerId}/balance-transactions/{transaction.Id}");
        }));

    private static void MapInvoices(RouteGroupBuilder invoices)
    {
        invoices.MapGet(string.Empty, async (
            [Description("Only this customer's invoices.")] Guid? customer,
            [Description("`draft`, `open`, `paid`, `void` or `uncollectible`.")] InvoiceStatus? status,
            [Description("An invoice number, such as `INV-2026-000123`.")] string? number,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListInvoicesHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(new ListInvoicesQuery(customer, status, number, cursor, limit), cancellationToken)).ToOk())
            .WithName("ListInvoices").WithSummary("List invoices").WithDescription("Newest first.").RequirePermission(TahsilPermissions.InvoicesRead);

        invoices.MapPost(string.Empty, async (CreateInvoiceRequest request, CreateInvoiceHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, invoice => Location("invoices", invoice.Id)))
            .WithName("CreateInvoice").WithSummary("Create a draft invoice")
            .WithDescription("Lines are one-time prices in the invoice's currency, or descriptions with unit amounts (negative for credits). The draft changes freely until it is finalized.")
            .ProducesValidationProblem().RequirePermission(TahsilPermissions.InvoicesWrite);

        invoices.MapGet("{id:guid}", async (Guid id, GetInvoiceHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk(response))
            .WithName("GetInvoice").WithSummary("Get an invoice").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.InvoicesRead);

        invoices.MapPatch("{id:guid}", async (Guid id, UpdateInvoiceRequest request, UpdateInvoiceHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(id, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdateInvoice").WithSummary("Update an invoice")
            .WithDescription("Drafts: the collection method, days until due, memo and metadata. Finalized invoices: the legal invoice the application issued, and metadata.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.InvoicesWrite);

        invoices.MapDelete("{id:guid}", async (Guid id, DeleteInvoiceHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToNoContent())
            .WithName("DeleteInvoice").WithSummary("Delete a draft invoice")
            .WithDescription("Its pending items wait for another invoice. Finalized invoices are voided instead.")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.InvoicesWrite);

        invoices.MapPost("{id:guid}/lines", async (Guid id, AddInvoiceLinesRequest request, AddInvoiceLinesHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, request, cancellationToken)).ToOk(response))
            .WithName("AddInvoiceLines").WithSummary("Add lines to a draft invoice")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.InvoicesWrite);

        invoices.MapDelete("{id:guid}/lines/{lineId:guid}", async (Guid id, Guid lineId, RemoveInvoiceLineHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, lineId, cancellationToken)).ToOk(response))
            .WithName("RemoveInvoiceLine").WithSummary("Remove a line from a draft invoice")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.InvoicesWrite);

        invoices.MapPost("{id:guid}/finalize", async (Guid id, FinalizeInvoiceHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk(response))
            .WithName("FinalizeInvoice").WithSummary("Finalize a draft invoice")
            .WithDescription("Assigns the number, takes the customer's billing details and applies the customer's balance. An invoice with nothing left to pay is paid at once.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.InvoicesWrite);

        invoices.MapPost("{id:guid}/void", async (Guid id, VoidInvoiceHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk(response))
            .WithName("VoidInvoice").WithSummary("Void an invoice")
            .WithDescription("For open and uncollectible invoices without payments. The customer's balance gets back what finalization took from it or added to it.")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.InvoicesWrite);

        invoices.MapPost("{id:guid}/mark-uncollectible", async (Guid id, MarkInvoiceUncollectibleHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk(response))
            .WithName("MarkInvoiceUncollectible").WithSummary("Mark an invoice uncollectible")
            .WithDescription("Stops collection; the customer can still pay it.")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.InvoicesWrite);
    }

    private static void MapInvoiceItems(RouteGroupBuilder items)
    {
        items.MapGet(string.Empty, async (
            [Description("Only this customer's items.")] Guid? customer,
            [Description("Only items no invoice has taken yet (true), or only invoiced ones (false).")] bool? pending,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListInvoiceItemsHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(new ListInvoiceItemsQuery(customer, pending, cursor, limit), cancellationToken)).ToOk())
            .WithName("ListInvoiceItems").WithSummary("List invoice items").RequirePermission(TahsilPermissions.InvoicesRead);

        items.MapPost(string.Empty, async (CreateInvoiceItemRequest request, CreateInvoiceItemHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, item => Location("invoice-items", item.Id)))
            .WithName("CreateInvoiceItem").WithSummary("Create a pending invoice item")
            .WithDescription("A charge or credit that joins the customer's next manual invoice created with `includePendingItems`.")
            .ProducesValidationProblem().RequirePermission(TahsilPermissions.InvoicesWrite);

        items.MapGet("{id:guid}", async (Guid id, GetInvoiceItemHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk(response))
            .WithName("GetInvoiceItem").WithSummary("Get an invoice item").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.InvoicesRead);

        items.MapDelete("{id:guid}", async (Guid id, DeleteInvoiceItemHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToNoContent())
            .WithName("DeleteInvoiceItem").WithSummary("Delete a pending invoice item")
            .WithDescription("Only while no invoice includes it.")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.InvoicesWrite);
    }

    private static void MapBalances(RouteGroupBuilder transactions)
    {
        transactions.MapGet(string.Empty, async (
            Guid customerId,
            [Description("Only this currency.")] string? currency,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListBalanceTransactionsHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(customerId, new ListBalanceTransactionsQuery(currency, cursor, limit), cancellationToken)).ToOk())
            .WithName("ListBalanceTransactions").WithSummary("List a customer's balance history").WithDescription("Newest first.")
            .ProducesProblem(StatusCodes.Status404NotFound).RequirePermission(TahsilPermissions.CustomersRead);

        transactions.MapPost(string.Empty, async (Guid customerId, CreateBalanceTransactionRequest request, AdjustBalanceHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(customerId, request, cancellationToken))
                .ToCreated(transaction => $"{TahsilApi.BasePath}/customers/{customerId}/balance-transactions/{transaction.Id}"))
            .WithName("AdjustBalance").WithSummary("Adjust a customer's balance")
            .WithDescription("Positive amounts give credit that the next invoices use; negative amounts are added to the next invoice. Balances never turn into refunds.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.InvoicesWrite);

        transactions.MapGet("{id:guid}", async (Guid customerId, Guid id, GetBalanceTransactionHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(customerId, id, cancellationToken)).ToOk())
            .WithName("GetBalanceTransaction").WithSummary("Get a balance transaction").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CustomersRead);
    }

    private static string Location(string collection, Guid id) => $"{TahsilApi.BasePath}/{collection}/{id}";
}
