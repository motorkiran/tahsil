using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Money;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Connections;

public sealed class GetRoutingHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<IReadOnlyDictionary<string, string>>> HandleAsync(CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ConnectionsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return Result.Success(await context.ReadRoutingAsync(cancellationToken));
    }
}

/// <summary>
/// Replaces which connection serves each currency (PAY-4). A connection serves only currencies its adapter supports,
/// and only while enabled; currencies left out are no longer routed.
/// </summary>
public sealed class UpdateRoutingHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, ProviderRegistry registry, BillingClock clock)
{
    public async Task<Result<IReadOnlyDictionary<string, string>>> HandleAsync(IReadOnlyDictionary<string, string> routing, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ConnectionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var keys = routing.Values.Distinct(StringComparer.Ordinal).ToList();
        var connections = await context.ProviderConnections.AsNoTracking().Where(connection => keys.Contains(connection.Key))
            .ToDictionaryAsync(connection => connection.Key, StringComparer.Ordinal, cancellationToken);
        foreach (var (currency, key) in routing.OrderBy(route => route.Key, StringComparer.Ordinal))
        {
            if (!Currency.TryGet(currency, out _))
            {
                return ConnectionErrors.UnknownCurrency(currency);
            }

            if (!connections.TryGetValue(key, out var connection))
            {
                return ConnectionErrors.UnknownConnection(currency);
            }

            if (!connection.IsEnabled)
            {
                return ConnectionErrors.Disabled(currency);
            }

            if (registry.Find(connection.Provider)?.Descriptor.Currencies.Contains(currency) != true)
            {
                return ConnectionErrors.CurrencyNotSupported(currency);
            }
        }

        var before = await context.ReadRoutingAsync(cancellationToken);
        if (before.Count == routing.Count && routing.All(route => before.TryGetValue(route.Key, out var key) && key == route.Value))
        {
            return Result.Success(before);
        }

        var now = clock.GetUtcNow();
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        var current = await context.ConnectionCurrencies.ToListAsync(cancellationToken);
        foreach (var route in current)
        {
            if (!routing.TryGetValue(route.Currency, out var key) || connections[key].Id != route.ConnectionId)
            {
                context.ConnectionCurrencies.Remove(route);
            }
        }

        // Removals go first, so a currency that moves to another connection never has two rows.
        await context.SaveChangesAsync(cancellationToken);
        foreach (var (currency, key) in routing)
        {
            if (!current.Any(route => route.Currency == currency && route.ConnectionId == connections[key].Id))
            {
                context.ConnectionCurrencies.Add(ConnectionCurrency.Create(currency, connections[key].Id, now));
            }
        }

        var after = routing.OrderBy(route => route.Key, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal);
        audit.Record(AuditActions.ConnectionRoutingUpdated, data: new Dictionary<string, object?> { ["before"] = before, ["after"] = after });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return CommonErrors.ConcurrencyConflict;
        }

        return after;
    }
}

internal static class RoutingQueries
{
    public static async Task<IReadOnlyDictionary<string, string>> ReadRoutingAsync(this ITahsilDbContext context, CancellationToken cancellationToken) =>
        (await context.ConnectionCurrencies.AsNoTracking()
            .Join(context.ProviderConnections, route => route.ConnectionId, connection => connection.Id, (route, connection) => new { route.Currency, connection.Key })
            .ToListAsync(cancellationToken))
        .OrderBy(route => route.Currency, StringComparer.Ordinal)
        .ToDictionary(route => route.Currency, route => route.Key, StringComparer.Ordinal);
}
