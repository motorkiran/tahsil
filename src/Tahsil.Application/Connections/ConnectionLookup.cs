using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Connections;

internal static class ConnectionLookup
{
    public static Task<ProviderConnection?> FindConnectionAsync(this ITahsilDbContext context, string idOrKey, CancellationToken cancellationToken) =>
        Guid.TryParse(idOrKey, out var id)
            ? context.ProviderConnections.SingleOrDefaultAsync(connection => connection.Id == id, cancellationToken)
            : context.ProviderConnections.SingleOrDefaultAsync(connection => connection.Key == idOrKey, cancellationToken);

    public static async Task<IReadOnlyList<string>> RoutedCurrenciesAsync(this ITahsilDbContext context, Guid connectionId, CancellationToken cancellationToken) =>
        await context.ConnectionCurrencies.AsNoTracking()
            .Where(route => route.ConnectionId == connectionId)
            .OrderBy(route => route.Currency)
            .Select(route => route.Currency)
            .ToListAsync(cancellationToken);

    /// <summary>The enabled connection that serves customer-present payments in a currency (PAY-4), if any.</summary>
    public static Task<ProviderConnection?> RoutedConnectionAsync(this ITahsilDbContext context, string currency, CancellationToken cancellationToken) =>
        context.ConnectionCurrencies
            .Where(route => route.Currency == currency)
            .Join(context.ProviderConnections, route => route.ConnectionId, connection => connection.Id, (_, connection) => connection)
            .Where(connection => connection.IsEnabled)
            .SingleOrDefaultAsync(cancellationToken);
}
