using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Model = Tahsil.Domain.Customers;

namespace Tahsil.Application.PaymentMethods;

public static class PaymentMethodErrors
{
    public static readonly Error NotFound = Error.NotFound("payment_method.not_found", "The payment method does not exist.");
    public static readonly Error NotActive = Error.Conflict("payment_method.not_active", "Only an active card can be the default; this one was removed or expired.");
}

/// <summary>A customer's saved cards, active first (CUS §2.3).</summary>
public sealed class ListPaymentMethodsHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<Page<PaymentMethodResponse>>> HandleAsync(Guid customerId, PaymentMethodStatus? status, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == customerId, cancellationToken) is not { } customer)
        {
            return Model.CustomerErrors.NotFound;
        }

        var methods = context.PaymentMethods.AsNoTracking().Where(method => method.CustomerId == customerId);
        if (status is { } filter)
        {
            var modelStatus = filter.Map<Model.PaymentMethodStatus>();
            methods = methods.Where(method => method.Status == modelStatus);
        }

        var list = await methods.ToListAsync(cancellationToken);
        return new Page<PaymentMethodResponse>(
            [.. list.OrderBy(method => method.Status).ThenByDescending(method => method.Id).Select(method => PaymentMethodMapper.ToResponse(method, customer.DefaultPaymentMethodId))],
            null);
    }
}

public sealed class GetPaymentMethodHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<PaymentMethodResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(method => method.Id == id, cancellationToken) is not { } method)
        {
            return PaymentMethodErrors.NotFound;
        }

        var defaultId = await context.Customers.AsNoTracking().Where(customer => customer.Id == method.CustomerId).Select(customer => customer.DefaultPaymentMethodId).SingleAsync(cancellationToken);
        return PaymentMethodMapper.ToResponse(method, defaultId);
    }
}

/// <summary>Makes an active card the customer's default (CUS §2.3).</summary>
public sealed class SetDefaultPaymentMethodHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, Payments.Dunning dunning, BillingClock clock)
{
    private static readonly string[] DefaultChanged = ["defaultPaymentMethod"];

    public async Task<Result<PaymentMethodResponse>> HandleAsync(Guid customerId, Guid paymentMethodId, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.Customers.SingleOrDefaultAsync(customer => customer.Id == customerId, cancellationToken) is not { } customer)
        {
            return Model.CustomerErrors.NotFound;
        }

        if (await context.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(method => method.Id == paymentMethodId && method.CustomerId == customerId, cancellationToken)
            is not { } method)
        {
            return PaymentMethodErrors.NotFound;
        }

        if (!method.IsChargeable)
        {
            return PaymentMethodErrors.NotActive;
        }

        var now = clock.GetUtcNow();
        customer.SetDefaultPaymentMethod(method.Id, now);
        audit.Record(AuditActions.CustomerUpdated, AuditSubject.Of(AuditSubjects.Customer, customer.Id), new Dictionary<string, object?>
        {
            ["changed"] = DefaultChanged,
        }, customerId: customer.Id);
        await context.SaveChangesAsync(cancellationToken);

        // Invoices waiting for the customer to act are tried with the new card at once (DUN-8).
        await dunning.RetryWaitingAsync(customer.Id, null, now, cancellationToken);
        return PaymentMethodMapper.ToResponse(method, customer.DefaultPaymentMethodId);
    }
}

/// <summary>
/// Removes a card (CUS-16): it is never charged again, stops being the default, and the provider is asked to delete
/// its token after this commits.
/// </summary>
public sealed class RemovePaymentMethodHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IOutbox outbox, BillingClock clock)
{
    public async Task<Result> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.RequireOrCustomer(TahsilPermissions.CustomersWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.PaymentMethods.SingleOrDefaultAsync(method => method.Id == id, cancellationToken) is not { } method || !authorizer.Owns(method.CustomerId))
        {
            return PaymentMethodErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        if (!method.Remove(now))
        {
            return Result.Success();
        }

        var customer = await context.Customers.SingleAsync(row => row.Id == method.CustomerId, cancellationToken);
        if (customer.DefaultPaymentMethodId == method.Id)
        {
            customer.SetDefaultPaymentMethod(null, now);
        }

        audit.Record(AuditActions.PaymentMethodRemoved, AuditSubject.Of(AuditSubjects.PaymentMethod, method.Id), new Dictionary<string, object?>
        {
            ["brand"] = method.Brand,
            ["last4"] = method.Last4,
        }, customerId: method.CustomerId);
        outbox.Enqueue(new DeleteProviderToken(method.Id));
        await context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>A removed card's token is deleted at the provider, best effort (CUS-16).</summary>
[OutboxMessage("payment_methods.delete_provider_token")]
public sealed record DeleteProviderToken(Guid PaymentMethodId);

public sealed partial class ProviderTokenDeleter(ITahsilDbContext context, ProviderAccess providers, SavedCards cards, ILogger<ProviderTokenDeleter> logger)
    : IOutboxMessageHandler<DeleteProviderToken>
{
    public async Task HandleAsync(DeleteProviderToken message, CancellationToken cancellationToken)
    {
        var method = await context.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(row => row.Id == message.PaymentMethodId, cancellationToken);
        if (method is not { Status: Model.PaymentMethodStatus.Removed })
        {
            return;
        }

        var connection = await context.ProviderConnections.AsNoTracking().SingleOrDefaultAsync(row => row.Id == method.ConnectionId, cancellationToken);
        if (connection is null || providers.For(connection) is not { } call)
        {
            LogNotDeleted(logger, method.Id, "the connection's adapter is not available");
            return;
        }

        var result = await call.Provider.RemoveSavedCardAsync(call.Context, cards.ReferenceOf(method), cancellationToken);
        if (!result.Removed)
        {
            LogNotDeleted(logger, method.Id, result.Message ?? "the provider did not remove it");
        }
    }

    [LoggerMessage(LogLevel.Warning, "The token of removed payment method {PaymentMethodId} was not deleted at the provider: {Reason}")]
    private static partial void LogNotDeleted(ILogger logger, Guid paymentMethodId, string reason);
}

/// <summary>Marks a card expired once its expiry passes (CUS-14); the expiry worker calls it.</summary>
public sealed class PaymentMethodExpiry(ITahsilDbContext context, BillingClock clock)
{
    public async Task ExpireAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await context.PaymentMethods.SingleOrDefaultAsync(method => method.Id == id, cancellationToken) is { } method)
        {
            method.Expire(clock.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}

public static class PaymentMethodMapper
{
    public static PaymentMethodResponse ToResponse(Model.PaymentMethod method, Guid? defaultId) => new(
        method.Id,
        method.CustomerId,
        method.ConnectionId,
        "card",
        method.Status.Map<PaymentMethodStatus>(),
        method.Id == defaultId,
        method.Brand,
        method.Funding,
        method.Bin,
        method.Last4,
        method.ExpMonth,
        method.ExpYear,
        method.ExpiresAt,
        method.Issuer,
        method.CardFamily,
        method.Country,
        method.RemovedAt,
        method.CreatedAt,
        method.UpdatedAt);
}

/// <summary>What the test API asks of the simulator (SIM-7, SIM-8); Test mode only.</summary>
public interface ISimulatorCards
{
    /// <summary>Saves a test card at the simulator; <see langword="null"/> when the number is not a test card.</summary>
    Task<Providers.Abstractions.SavedCard?> SaveAsync(Guid connectionId, string number, int expMonth, int expYear, CancellationToken cancellationToken);

    /// <summary>Changes how a saved card answers; <see langword="false"/> for an unknown behavior or card.</summary>
    Task<bool> SetBehaviorAsync(Guid connectionId, string token, string behavior, CancellationToken cancellationToken);
}

public static class TestApiErrors
{
    public static readonly Error NotTestMode = Error.NotFound("request.not_found", "The test API exists in Test mode only.");
    public static readonly Error NoSimulator = Error.Conflict("simulator.unavailable", "No enabled simulator connection exists.");
    public static readonly Error NotATestCard = Error.Validation("simulator.not_a_test_card", "Only simulator test cards are accepted.", "card");
    public static readonly Error NotASimulatorCard = Error.Validation("simulator.not_a_simulator_card", "The payment method was not saved by the simulator.", "id");
    public static readonly Error UnknownBehavior = Error.Validation(
        "simulator.unknown_behavior", "The behavior is succeed, a failure code such as insufficient_funds, timeout_after_charge, timeout_before_charge or pending.", "behavior");
}

/// <summary>Creates saved simulator cards for API-only tests, with a consent record marked as made by the test API (SIM-7).</summary>
public sealed class CreateTestPaymentMethodHandler(
    ITahsilDbContext context, IAuthorizer authorizer, ProviderRegistry registry, ISimulatorCards simulator, SavedCards cards, BillingClock clock)
{
    public async Task<Result<PaymentMethodResponse>> HandleAsync(CreateSimulatorPaymentMethodRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.TestWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (registry.Mode != TahsilMode.Test)
        {
            return TestApiErrors.NotTestMode;
        }

        if (await context.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == request.Customer && customer.AnonymizedAt == null, cancellationToken)
            is not { } customer)
        {
            return Model.CustomerErrors.NotFound;
        }

        var connection = await context.ProviderConnections.AsNoTracking()
            .Where(row => row.Provider == ProviderRegistry.Simulator && row.IsEnabled)
            .OrderBy(row => row.Key != "simulator").ThenBy(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection is null)
        {
            return TestApiErrors.NoSimulator;
        }

        var now = clock.GetUtcNow();
        if (await simulator.SaveAsync(connection.Id, request.Card, request.ExpMonth ?? 12, request.ExpYear ?? now.Year + 4, cancellationToken) is not { } saved)
        {
            return TestApiErrors.NotATestCard;
        }

        var consent = Model.ConsentRecord.Create(customer.Id, Model.ConsentType.CardStorage, "Created by the test API", new Dictionary<string, string> { ["source"] = "test_api" }, null, null, now);
        context.ConsentRecords.Add(consent);
        var method = await cards.KeepAsync(customer.Id, connection.Id, saved, consent.Id, now, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        var defaultId = await context.Customers.AsNoTracking().Where(row => row.Id == customer.Id).Select(row => row.DefaultPaymentMethodId).SingleAsync(cancellationToken);
        return PaymentMethodMapper.ToResponse(method!, defaultId);
    }
}

/// <summary>Changes how a saved simulator card answers from now on, for scripted dunning scenarios (SIM-8).</summary>
public sealed class UpdateTestPaymentMethodHandler(ITahsilDbContext context, IAuthorizer authorizer, ProviderRegistry registry, ISimulatorCards simulator, SavedCards cards)
{
    public async Task<Result<PaymentMethodResponse>> HandleAsync(Guid id, UpdateSimulatorPaymentMethodRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.TestWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (registry.Mode != TahsilMode.Test)
        {
            return TestApiErrors.NotTestMode;
        }

        if (await context.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(method => method.Id == id, cancellationToken) is not { } method)
        {
            return PaymentMethodErrors.NotFound;
        }

        var connection = await context.ProviderConnections.AsNoTracking().SingleAsync(row => row.Id == method.ConnectionId, cancellationToken);
        if (connection.Provider != ProviderRegistry.Simulator)
        {
            return TestApiErrors.NotASimulatorCard;
        }

        if (!await simulator.SetBehaviorAsync(connection.Id, cards.ReferenceOf(method).Token, request.Behavior, cancellationToken))
        {
            return TestApiErrors.UnknownBehavior;
        }

        var defaultId = await context.Customers.AsNoTracking().Where(row => row.Id == method.CustomerId).Select(row => row.DefaultPaymentMethodId).SingleAsync(cancellationToken);
        return PaymentMethodMapper.ToResponse(method, defaultId);
    }
}
