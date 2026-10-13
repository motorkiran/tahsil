using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Application.Coupons;
using Tahsil.Application.Invoices;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;
using Invoice = Tahsil.Domain.Invoices.Invoice;
using Model = Tahsil.Domain.Subscriptions;
using ModelInvoices = Tahsil.Domain.Invoices;

namespace Tahsil.Application.Subscriptions;

/// <summary>
/// Creates a subscription through the API (SUB §1): with a trial it starts trialing; otherwise its first invoice is
/// finalized at once and, when charged automatically, paid with the card during the request (SUB-6), or sent (SUB-7).
/// </summary>
public sealed class CreateSubscriptionHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IIdempotency idempotency,
    SubscriptionLines lines,
    SubscriptionMapper mapper,
    InvoiceFinalizer finalizer,
    MerchantPayments payments,
    ChargeableCards cards,
    Redemptions redemptions,
    Kimlik.KimlikPlanRule kimlikPlans,
    IOptions<BillingOptions> billing,
    IOptions<SubscriptionOptions> options,
    BillingClock clock)
{
    /// <summary>The longest trial, so that dates stay meaningful.</summary>
    public const int MaxTrialDays = 730;

    public async Task<Result<Versioned<SubscriptionResponse>>> HandleAsync(CreateSubscriptionRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.SubscriptionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var found = await InvoiceCustomers.FindAsync(context, request.Customer, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var customer = found.Value;
        var metadata = Metadata.Validate(request.Metadata);
        if (metadata.IsFailure)
        {
            return metadata.Error;
        }

        var items = await SubscriptionItemRequests.ResolveAsync(context, request.Items ?? [], cancellationToken);
        if (items.IsFailure)
        {
            return items.Error;
        }

        if (SubscriptionItems.Validate(items.Value, currency: null) is { } invalidItems)
        {
            return invalidItems;
        }

        if (await kimlikPlans.CheckAsync(customer.Id, customer.KimlikSubscriber, items.Value, null, cancellationToken) is { } kimlikConflict)
        {
            return kimlikConflict;
        }

        (Guid CouponId, Guid? CodeId)? coupon = null;
        if (request.Coupon is not null || request.PromotionCode is not null)
        {
            var named = await redemptions.FindAsync(request.Coupon, request.PromotionCode, cancellationToken);
            if (named.IsFailure)
            {
                return named.Error;
            }

            coupon = named.Value;
        }

        var now = clock.GetUtcNow();
        if (request.TrialDays is < 0 or > MaxTrialDays || request.TrialEnd > now.AddDays(MaxTrialDays))
        {
            return SubscriptionErrors.Invalid(request.TrialDays is null ? "trialEnd" : "trialDays", $"A trial lasts at most {MaxTrialDays} days.");
        }

        var zone = billing.Value.Zone;
        var trialEnd = SubscriptionTrial.Resolve(request.TrialEnd, request.TrialDays, items.Value.Select(item => item.Price), zone, now);
        var currency = items.Value[0].Price.Currency;
        var collectionMethod = request.CollectionMethod.Map<ModelInvoices.CollectionMethod>();
        var chargesNow = trialEnd is null && collectionMethod == ModelInvoices.CollectionMethod.ChargeAutomatically;
        var method = await PaymentMethodAsync(request.DefaultPaymentMethod, customer, currency, chargesNow, cancellationToken);
        if (method.IsFailure)
        {
            return method.Error;
        }

        var created = Subscription.Create(
            customer.Id,
            items.Value,
            new SubscriptionTerms
            {
                CollectionMethod = collectionMethod,
                DaysUntilDue = request.DaysUntilDue,
                DefaultPaymentMethodId = request.DefaultPaymentMethod,
                TrialEnd = trialEnd,
                TrialEndBehavior = request.TrialEndBehavior.Map<Model.TrialEndBehavior>(),
                BillingCycleAnchor = request.BillingCycleAnchor,
                Metadata = metadata.Value,
            },
            zone,
            options.Value.IncompleteExpiry,
            now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var subscription = created.Value;
        Invoice? invoice = null;
        await using (var transaction = await context.BeginTransactionAsync(cancellationToken))
        {
            context.Subscriptions.Add(subscription);
            idempotency.RecordCreated(ResourceTypes.Subscription, subscription.Id);
            audit.Record(AuditActions.SubscriptionCreated, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
            {
                ["status"] = SubscriptionMapper.Wire(subscription.Status),
                ["items"] = subscription.Items.Select(item => new Dictionary<string, object?> { ["price"] = item.PriceId, ["quantity"] = item.Quantity }).ToList(),
                ["currentPeriodEnd"] = subscription.CurrentPeriodEnd,
                ["trialEnd"] = subscription.TrialEnd,
            }, customerId: customer.Id);
            if (coupon is { } redeem && await redemptions.ApplyToSubscriptionAsync(subscription, redeem, items.Value, cancellationToken) is { IsFailure: true } refused)
            {
                return refused.Error;
            }

            // Saved first, so that what follows the first invoice's payment finds it.
            await context.SaveChangesAsync(cancellationToken);
            if (trialEnd is null)
            {
                var first = await FirstInvoiceAsync(subscription, customer, now, cancellationToken);
                if (first.IsFailure)
                {
                    return first.Error;
                }

                invoice = first.Value;
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (invoice is { Status: ModelInvoices.InvoiceStatus.Open, CollectionMethod: ModelInvoices.CollectionMethod.ChargeAutomatically })
        {
            // A decline leaves the subscription incomplete with the invoice to pay on its page; an unanswered charge is reconciled (SUB-6).
            await payments.ChargeAsync(invoice.Id, method.Value!.Id, cancellationToken);
        }

        return new Versioned<SubscriptionResponse>(await mapper.ToResponseAsync(subscription, cancellationToken), subscription.Version);
    }

    /// <summary>
    /// The first period's invoice (INV-1, SUB-4), finalized at once without a draft period. Without licensed items there
    /// is nothing to bill in advance, and the subscription is active at once.
    /// </summary>
    private async Task<Result<Invoice?>> FirstInvoiceAsync(Subscription subscription, Customer customer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var period = new BillingPeriod(subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd);
        var drafts = await lines.LicensedAsync(subscription, period, customer.Locale, cancellationToken);
        if (drafts.Count == 0)
        {
            if (subscription.Status == Model.SubscriptionStatus.Incomplete)
            {
                subscription.Activate(now);
            }

            return Result.Success<Invoice?>(null);
        }

        var invoice = Invoice.CreateDraft(
            customer.Id,
            subscription.Currency,
            ModelInvoices.BillingReason.SubscriptionCreate,
            subscription.CollectionMethod,
            subscription.DaysUntilDue,
            memo: null,
            autoAdvance: false,
            Metadata.Empty,
            now,
            subscription.Id,
            period,
            discount: await redemptions.OfSubscriptionAsync(subscription.DiscountId, cancellationToken)).Value;
        invoice.AddLines(drafts, customer.IsTaxExempt, now);
        context.Invoices.Add(invoice);
        subscription.SetLatestInvoice(invoice.Id, now);
        if (await finalizer.FinalizeAsync(invoice, cancellationToken) is { IsFailure: true } failed)
        {
            return failed.Error;
        }

        if (invoice.Status == ModelInvoices.InvoiceStatus.Open && invoice.CollectionMethod == ModelInvoices.CollectionMethod.ChargeAutomatically)
        {
            // This request charges it; the payment worker leaves it alone.
            invoice.ClaimPaymentAttempt(now);
        }

        return invoice;
    }

    /// <summary>The card given, which must be usable; or, when a charge is due now, the customer's usable default (SUB-5).</summary>
    private async Task<Result<PaymentMethod?>> PaymentMethodAsync(Guid? requested, Customer customer, string currency, bool required, CancellationToken cancellationToken)
    {
        if (requested is { } id)
        {
            return await UsableAsync(id) is { } given ? given : SubscriptionErrors.PaymentMethodUnusable;
        }

        if (!required)
        {
            return Result.Success<PaymentMethod?>(null);
        }

        return await UsableAsync(customer.DefaultPaymentMethodId) is { } method ? method : SubscriptionErrors.PaymentMethodRequired;

        async Task<PaymentMethod?> UsableAsync(Guid? methodId) => (await cards.FindAsync(methodId, customer.Id, currency, cancellationToken))?.Method;
    }
}

public sealed class GetSubscriptionHandler(ITahsilDbContext context, IAuthorizer authorizer, SubscriptionMapper mapper)
{
    public async Task<Result<Versioned<SubscriptionResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.SubscriptionsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.Subscriptions.AsNoTracking().Include(subscription => subscription.Items).SingleOrDefaultAsync(subscription => subscription.Id == id, cancellationToken) is { } found
            ? new Versioned<SubscriptionResponse>(await mapper.ToResponseAsync(found, cancellationToken), found.Version)
            : SubscriptionErrors.NotFound;
    }
}

public sealed record ListSubscriptionsQuery(
    Guid? Customer = null, Contracts.SubscriptionStatus? Status = null, string? Price = null, string? Product = null, string? Cursor = null, int? Limit = null);

public sealed class ListSubscriptionsHandler(ITahsilDbContext context, IAuthorizer authorizer, SubscriptionMapper mapper)
{
    private static readonly Keyset<Subscription, Guid> Newest = new("newest", subscription => subscription.Id, subscription => subscription.Id, Descending: true);

    public async Task<Result<Page<SubscriptionResponse>>> HandleAsync(ListSubscriptionsQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.SubscriptionsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Paging.IsValidLimit(query.Limit ?? Paging.DefaultLimit))
        {
            return CommonErrors.InvalidLimit();
        }

        var subscriptions = context.Subscriptions.AsNoTracking().Include(subscription => subscription.Items).AsQueryable();
        if (query.Customer is { } customer)
        {
            subscriptions = subscriptions.Where(subscription => subscription.CustomerId == customer);
        }

        if (query.Status is { } status)
        {
            var modelStatus = status.Map<Model.SubscriptionStatus>();
            subscriptions = subscriptions.Where(subscription => subscription.Status == modelStatus);
        }

        if (query.Price is { } priceKey)
        {
            var priceId = (await context.FindPriceAsync(priceKey, cancellationToken))?.Id;
            subscriptions = subscriptions.Where(subscription => subscription.Items.Any(item => item.PriceId == priceId));
        }

        if (query.Product is { } productKey)
        {
            var productId = (await context.FindProductAsync(productKey, cancellationToken))?.Id;
            var prices = context.Prices.Where(price => price.ProductId == productId).Select(price => price.Id);
            subscriptions = subscriptions.Where(subscription => subscription.Items.Any(item => prices.Contains(item.PriceId)));
        }

        var page = await Newest.ReadAsync(subscriptions, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<SubscriptionResponse>(await mapper.ToResponsesAsync(page.Items, cancellationToken), page.NextCursor);
    }
}

/// <summary>Finds the prices and products of requested items.</summary>
internal static class SubscriptionItemRequests
{
    public static async Task<Result<List<SubscriptionItemSpec>>> ResolveAsync(
        ITahsilDbContext context, IReadOnlyList<SubscriptionItemRequest> requests, CancellationToken cancellationToken)
    {
        var specs = new List<SubscriptionItemSpec>();
        for (var index = 0; index < requests.Count; index++)
        {
            if (await context.FindPriceAsync(requests[index].Price, cancellationToken) is not { } price)
            {
                return SubscriptionErrors.InvalidItems($"items[{index}].price", "No price has this ID or lookup key.");
            }

            var product = await context.Products.AsNoTracking().SingleAsync(row => row.Id == price.ProductId, cancellationToken);
            specs.Add(new SubscriptionItemSpec(price, product, requests[index].Quantity));
        }

        return specs;
    }
}

public sealed class SubscriptionMapper(ITahsilDbContext context, InvoiceLinks links)
{
    public async Task<SubscriptionResponse> ToResponseAsync(Subscription subscription, CancellationToken cancellationToken) =>
        (await ToResponsesAsync([subscription], cancellationToken))[0];

    public async Task<List<SubscriptionResponse>> ToResponsesAsync(IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        var priceIds = subscriptions.SelectMany(subscription => subscription.Items.Select(item => item.PriceId)
            .Concat(subscription.ScheduledChange?.Items.Select(item => item.PriceId) ?? [])).Distinct().ToList();
        var prices = await context.Prices.AsNoTracking().Where(price => priceIds.Contains(price.Id)).ToDictionaryAsync(price => price.Id, cancellationToken);
        var productIds = prices.Values.Select(price => price.ProductId).Distinct().ToList();
        var products = await context.Products.AsNoTracking().Where(product => productIds.Contains(product.Id)).ToDictionaryAsync(product => product.Id, cancellationToken);
        var invoiceIds = subscriptions.Where(subscription => subscription.LatestInvoiceId is not null).Select(subscription => subscription.LatestInvoiceId!.Value).ToList();
        var invoices = await context.Invoices.AsNoTracking().Where(invoice => invoiceIds.Contains(invoice.Id)).ToDictionaryAsync(invoice => invoice.Id, cancellationToken);
        var discounts = await DiscountsAsync(subscriptions, cancellationToken);
        return [.. subscriptions.Select(subscription => ToResponse(subscription, prices, products, invoices, discounts))];
    }

    /// <summary>The subscriptions' discounts that still last, with their coupons.</summary>
    private async Task<Dictionary<Guid, DiscountResponse>> DiscountsAsync(IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        var discountIds = subscriptions.Where(subscription => subscription.DiscountId is not null).Select(subscription => subscription.DiscountId!.Value).ToList();
        var discounts = await context.Discounts.AsNoTracking().Where(discount => discountIds.Contains(discount.Id) && discount.EndedAt == null).ToListAsync(cancellationToken);
        var couponIds = discounts.Select(discount => discount.CouponId).Distinct().ToList();
        var coupons = await context.ToResponsesAsync(await context.Coupons.AsNoTracking().Where(coupon => couponIds.Contains(coupon.Id)).ToListAsync(cancellationToken), cancellationToken);
        return discounts.ToDictionary(
            discount => discount.Id,
            discount => new DiscountResponse(
                discount.Id, coupons.Single(coupon => coupon.Id == discount.CouponId), discount.PromotionCodeId, discount.CyclesRemaining, discount.StartedAt, discount.EndedAt));
    }

    private static SubscriptionPriceResponse Price(Guid priceId, Dictionary<Guid, Domain.Catalog.Price> prices, Dictionary<Guid, Domain.Catalog.Product> products)
    {
        var price = prices[priceId];
        var product = products[price.ProductId];
        return new SubscriptionPriceResponse(price.Id, price.Key, price.Name, new ProductSummary(product.Id, product.Key, product.Name));
    }

    /// <summary>A value as events and the API write it, such as <c>past_due</c>.</summary>
    internal static string Wire(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private SubscriptionResponse ToResponse(
        Subscription subscription,
        Dictionary<Guid, Domain.Catalog.Price> prices,
        Dictionary<Guid, Domain.Catalog.Product> products,
        Dictionary<Guid, Invoice> invoices,
        Dictionary<Guid, DiscountResponse> discounts) => new()
        {
            Id = subscription.Id,
            CustomerId = subscription.CustomerId,
            Status = subscription.Status.Map<Contracts.SubscriptionStatus>(),
            Currency = subscription.Currency,
            Items = [.. subscription.Items.Select(item => new SubscriptionItemResponse(item.Id, Price(item.PriceId, prices, products), item.Quantity))],
            CollectionMethod = subscription.CollectionMethod.Map<CollectionMethod>(),
            DaysUntilDue = subscription.DaysUntilDue,
            DefaultPaymentMethodId = subscription.DefaultPaymentMethodId,
            BillingCycleAnchor = subscription.BillingCycleAnchor,
            CurrentPeriodStart = subscription.CurrentPeriodStart,
            CurrentPeriodEnd = subscription.CurrentPeriodEnd,
            TrialStart = subscription.TrialStart,
            TrialEnd = subscription.TrialEnd,
            TrialEndBehavior = subscription.TrialEndBehavior.Map<Contracts.TrialEndBehavior>(),
            CancelAtPeriodEnd = subscription.CancelAtPeriodEnd,
            CancelAt = subscription.CancelAt,
            CanceledAt = subscription.CanceledAt,
            EndedAt = subscription.EndedAt,
            CancellationReason = subscription.CancellationReason?.Map<Contracts.CancellationReason>(),
            CancellationFeedback = subscription.CancellationFeedback?.Map<Contracts.CancellationFeedback>(),
            CancellationComment = subscription.CancellationComment,
            ScheduledChange = subscription.ScheduledChange is { } change
            ? new ScheduledChangeResponse([.. change.Items.Select(item => new ScheduledChangeItemResponse(Price(item.PriceId, prices, products), item.Quantity))], change.EffectiveAt)
            : null,
            LatestInvoice = subscription.LatestInvoiceId is { } invoiceId && invoices.TryGetValue(invoiceId, out var invoice)
            ? new LatestInvoiceResponse(invoice.Id, invoice.Number, invoice.Status.Map<InvoiceStatus>(), invoice.AmountDue, links.UrlFor(invoice))
            : null,
            Discount = subscription.DiscountId is { } discountId ? discounts.GetValueOrDefault(discountId) : null,
            IncompleteExpiresAt = subscription.IncompleteExpiresAt,
            PastDueSince = subscription.PastDueSince,
            CheckoutSessionId = subscription.CheckoutSessionId,
            Metadata = subscription.Metadata,
            CreatedAt = subscription.CreatedAt,
            UpdatedAt = subscription.UpdatedAt,
        };
}
