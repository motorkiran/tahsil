using System.ComponentModel;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Tax rates, meters, products and prices. <c>{idOrKey}</c> accepts an ID or a key.</summary>
internal static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder api)
    {
        MapTaxRates(api.MapGroup("tax-rates").WithTags("Tax rates"));
        MapMeters(api.MapGroup("meters").WithTags("Meters"));
        MapProducts(api.MapGroup("products").WithTags("Products"));
        MapPrices(api.MapGroup("prices").WithTags("Prices"));
        return api;
    }

    /// <summary>Answers a retried create whose first attempt died after committing (API-13) with the resource as it is now.</summary>
    public static IServiceCollection AddCatalogResourceReaders(this IServiceCollection services) => services
        .AddSingleton(Reader<GetTaxRateHandler, TaxRateResponse>(ResourceTypes.TaxRate, "tax-rates", (handler, id, token) => handler.HandleAsync(id, token)))
        .AddSingleton(Reader<GetMeterHandler, MeterResponse>(ResourceTypes.Meter, "meters", (handler, id, token) => handler.HandleAsync(id, token)))
        .AddSingleton(Reader<GetProductHandler, ProductResponse>(ResourceTypes.Product, "products", (handler, id, token) => handler.HandleAsync(id, token)))
        .AddSingleton(Reader<GetPriceHandler, PriceResponse>(ResourceTypes.Price, "prices", (handler, id, token) => handler.HandleAsync(id, token)));

    private static void MapTaxRates(RouteGroupBuilder rates)
    {
        rates.MapGet(string.Empty, async (
            [Description("Only archived (true) or current (false) rates.")] bool? archived,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListTaxRatesHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(new ListCatalogQuery(archived, cursor, limit), cancellationToken)).ToOk())
            .WithName("ListTaxRates").WithSummary("List tax rates").RequirePermission(TahsilPermissions.CatalogRead);

        rates.MapPost(string.Empty, async (CreateTaxRateRequest request, CreateTaxRateHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, rate => Location("tax-rates", rate.Id)))
            .WithName("CreateTaxRate").WithSummary("Create a tax rate")
            .WithDescription("The percentage and whether prices include the tax never change: to change a rate, create a new one.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CatalogWrite);

        rates.MapGet("{idOrKey}", async (string idOrKey, GetTaxRateHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(idOrKey, cancellationToken)).ToOk(response))
            .WithName("GetTaxRate").WithSummary("Get a tax rate").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CatalogRead);

        rates.MapPatch("{idOrKey}", async (string idOrKey, UpdateTaxRateRequest request, UpdateTaxRateHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(idOrKey, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdateTaxRate").WithSummary("Update a tax rate")
            .WithDescription("Changes the name, description or archive flag. Archived rates cannot be given to new prices.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.CatalogWrite);
    }

    private static void MapMeters(RouteGroupBuilder meters)
    {
        meters.MapGet(string.Empty, async (
            [Description("Only archived (true) or current (false) meters.")] bool? archived,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListMetersHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(new ListCatalogQuery(archived, cursor, limit), cancellationToken)).ToOk())
            .WithName("ListMeters").WithSummary("List meters").RequirePermission(TahsilPermissions.CatalogRead);

        meters.MapPost(string.Empty, async (CreateMeterRequest request, CreateMeterHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, meter => Location("meters", meter.Id)))
            .WithName("CreateMeter").WithSummary("Create a meter")
            .WithDescription("The key is the name usage events report; it, the aggregation and the counted property never change.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CatalogWrite);

        meters.MapGet("{idOrKey}", async (string idOrKey, GetMeterHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(idOrKey, cancellationToken)).ToOk(response))
            .WithName("GetMeter").WithSummary("Get a meter").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CatalogRead);

        meters.MapPatch("{idOrKey}", async (string idOrKey, UpdateMeterRequest request, UpdateMeterHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(idOrKey, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdateMeter").WithSummary("Update a meter")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.CatalogWrite);
    }

    private static void MapProducts(RouteGroupBuilder products)
    {
        products.MapGet(string.Empty, async (
            [Description("Only archived (true) or current (false) products.")] bool? archived,
            [Description("Matches the key or name, ignoring case.")] string? q,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListProductsHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(new ListProductsQuery(archived, q, cursor, limit), cancellationToken)).ToOk())
            .WithName("ListProducts").WithSummary("List products").RequirePermission(TahsilPermissions.CatalogRead);

        products.MapPost(string.Empty, async (CreateProductRequest request, CreateProductHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, product => Location("products", product.Id)))
            .WithName("CreateProduct").WithSummary("Create a product")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CatalogWrite);

        products.MapGet("{idOrKey}", async (string idOrKey, GetProductHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(idOrKey, cancellationToken)).ToOk(response))
            .WithName("GetProduct").WithSummary("Get a product").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CatalogRead);

        products.MapPatch("{idOrKey}", async (string idOrKey, UpdateProductRequest request, UpdateProductHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(idOrKey, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdateProduct").WithSummary("Update a product")
            .WithDescription("JSON Merge Patch: omitted fields stay, null clears. Archiving stops new sales; existing subscriptions keep renewing.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.CatalogWrite);

        products.MapDelete("{idOrKey}", async (string idOrKey, DeleteProductHandler handler, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(idOrKey, cancellationToken)).ToNoContent())
            .WithName("DeleteProduct").WithSummary("Delete a product")
            .WithDescription("Deletes the product and its prices while none of them was ever used; otherwise archive it.")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CatalogWrite);
    }

    private static void MapPrices(RouteGroupBuilder prices)
    {
        prices.MapGet(string.Empty, async (
            [Description("The product's ID or key.")] string? product,
            [Description("An ISO 4217 code, such as TRY.")] string? currency,
            [Description("`one_time` or `recurring`.")] string? type,
            [Description("Only archived (true) or current (false) prices.")] bool? archived,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListPricesHandler handler,
            CancellationToken cancellationToken) =>
            QueryValues.TryParseEnum<PriceType>(type, out var typeFilter)
                ? (await handler.HandleAsync(new ListPricesQuery(product, currency, typeFilter, archived, cursor, limit), cancellationToken)).ToOk()
                : ApiResults.Problem(CommonErrors.InvalidParameter(nameof(type))))
            .WithName("ListPrices").WithSummary("List prices").RequirePermission(TahsilPermissions.CatalogRead);

        prices.MapPost(string.Empty, async (CreatePriceRequest request, CreatePriceHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, price => Location("prices", price.Id)))
            .WithName("CreatePrice").WithSummary("Create a price")
            .WithDescription("Amounts are integers in minor units (29900 is ₺299.00). Only the key, name, trial, archive flag and metadata change later: "
                + "a new amount is a new price. With transferKey, the lookup key moves from the price that holds it.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CatalogWrite);

        prices.MapGet("{idOrKey}", async (string idOrKey, GetPriceHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(idOrKey, cancellationToken)).ToOk(response))
            .WithName("GetPrice").WithSummary("Get a price").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CatalogRead);

        prices.MapPatch("{idOrKey}", async (string idOrKey, UpdatePriceRequest request, UpdatePriceHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(idOrKey, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdatePrice").WithSummary("Update a price")
            .WithDescription("Changes the lookup key, name, trial, archive flag or metadata; any other field answers price.immutable.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.CatalogWrite);
    }

    private static string Location(string collection, Guid id) => $"{TahsilApi.BasePath}/{collection}/{id}";

    private static IdempotentResourceReader Reader<THandler, TResponse>(
        string resourceType, string collection, Func<THandler, string, CancellationToken, Task<Domain.Common.Result<Versioned<TResponse>>>> get)
        where THandler : notnull =>
        new(resourceType, async (http, id) =>
        {
            var result = await get(http.RequestServices.GetRequiredService<THandler>(), id.ToString(), http.RequestAborted);
            return result.ToCreated(http.Response, _ => Location(collection, id));
        });
}
