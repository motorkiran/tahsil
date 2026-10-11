using System.ComponentModel;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Application.Coupons;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>Coupons and the promotion codes that redeem them (CPN §8). Coupons are audited but are not webhook events.</summary>
internal static class CouponEndpoints
{
    public static IEndpointRouteBuilder MapCouponEndpoints(this IEndpointRouteBuilder api)
    {
        MapCoupons(api.MapGroup("coupons").WithTags("Coupons"));
        MapPromotionCodes(api.MapGroup("promotion-codes").WithTags("Promotion codes"));
        return api;
    }

    /// <summary>Answers a retried create whose first attempt died after committing (API-13) with the resource as it is now.</summary>
    public static IServiceCollection AddCouponResourceReaders(this IServiceCollection services) => services
        .AddSingleton(new IdempotentResourceReader(ResourceTypes.Coupon, async (http, id) =>
            (await http.RequestServices.GetRequiredService<GetCouponHandler>().HandleAsync(id.ToString(), http.RequestAborted))
                .ToCreated(http.Response, _ => Location("coupons", id))))
        .AddSingleton(new IdempotentResourceReader(ResourceTypes.PromotionCode, async (http, id) =>
            (await http.RequestServices.GetRequiredService<GetPromotionCodeHandler>().HandleAsync(id, http.RequestAborted))
                .ToCreated(http.Response, _ => Location("promotion-codes", id))));

    private static void MapCoupons(RouteGroupBuilder coupons)
    {
        coupons.MapGet(string.Empty, async (
            [Description("Only archived (true) or current (false) coupons.")] bool? archived,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListCouponsHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(new ListCatalogQuery(archived, cursor, limit), cancellationToken)).ToOk())
            .WithName("ListCoupons").WithSummary("List coupons").RequirePermission(TahsilPermissions.CouponsRead);

        coupons.MapPost(string.Empty, async (CreateCouponRequest request, CreateCouponHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, coupon => Location("coupons", coupon.Id)))
            .WithName("CreateCoupon").WithSummary("Create a coupon")
            .WithDescription("A percentage or an amount off, once, for a number of cycles or forever. The discount, duration and products never change.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CouponsWrite);

        coupons.MapGet("{idOrKey}", async (string idOrKey, GetCouponHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(idOrKey, cancellationToken)).ToOk(response))
            .WithName("GetCoupon").WithSummary("Get a coupon").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CouponsRead);

        coupons.MapPatch("{idOrKey}", async (string idOrKey, UpdateCouponRequest request, UpdateCouponHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(idOrKey, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdateCoupon").WithSummary("Update a coupon")
            .WithDescription("JSON Merge Patch of the name, limits, archive flag and metadata. Archived coupons and their codes redeem no more; existing discounts continue.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.CouponsWrite);
    }

    private static void MapPromotionCodes(RouteGroupBuilder codes)
    {
        codes.MapGet(string.Empty, async (
            [Description("The coupon's ID or key.")] string? coupon,
            [Description("Only active (true) or inactive (false) codes.")] bool? active,
            [Description("A code, ignoring case.")] string? code,
            [Description("The `nextCursor` of the previous page.")] string? cursor,
            [Description("Page size, 50 by default and at most 200.")] int? limit,
            ListPromotionCodesHandler handler,
            CancellationToken cancellationToken) =>
            (await handler.HandleAsync(new ListPromotionCodesQuery(coupon, active, code, cursor, limit), cancellationToken)).ToOk())
            .WithName("ListPromotionCodes").WithSummary("List promotion codes").RequirePermission(TahsilPermissions.CouponsRead);

        codes.MapPost(string.Empty, async (CreatePromotionCodeRequest request, CreatePromotionCodeHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(request, cancellationToken)).ToCreated(response, code => Location("promotion-codes", code.Id)))
            .WithName("CreatePromotionCode").WithSummary("Create a promotion code")
            .WithDescription("Codes are stored in uppercase and unique ignoring case across all coupons. Restrictions never change; deactivate a code and create another.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.CouponsWrite);

        codes.MapGet("{id:guid}", async (Guid id, GetPromotionCodeHandler handler, HttpResponse response, CancellationToken cancellationToken) =>
            (await handler.HandleAsync(id, cancellationToken)).ToOk(response))
            .WithName("GetPromotionCode").WithSummary("Get a promotion code").ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.CouponsRead);

        codes.MapPatch("{id:guid}", async (Guid id, UpdatePromotionCodeRequest request, UpdatePromotionCodeHandler handler, HttpContext http, CancellationToken cancellationToken) =>
            http.Request.ReadIfMatch() is { IsFailure: true } ifMatch
                ? ApiResults.Problem(ifMatch.Error)
                : (await handler.HandleAsync(id, request, http.Request.ReadIfMatch().Value, cancellationToken)).ToOk(http.Response))
            .WithName("UpdatePromotionCode").WithSummary("Activate or deactivate a promotion code")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(TahsilPermissions.CouponsWrite);
    }

    private static string Location(string collection, Guid id) => $"{TahsilApi.BasePath}/{collection}/{id}";
}
