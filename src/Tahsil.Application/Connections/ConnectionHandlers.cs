using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Payments;
using Model = Tahsil.Domain.Payments;

namespace Tahsil.Application.Connections;

public sealed class ListProvidersHandler(ProviderRegistry registry, IAuthorizer authorizer)
{
    public Result<Page<ProviderResponse>> Handle()
    {
        if (authorizer.Require(TahsilPermissions.ConnectionsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return new Page<ProviderResponse>([.. registry.All.Select(provider => ConnectionMapper.ToResponse(provider.Descriptor))], null);
    }
}

public sealed class CreateConnectionHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IIdempotency idempotency,
    ProviderRegistry registry,
    ConnectionSecrets secrets,
    ConnectionMapper mapper,
    BillingClock clock)
{
    public async Task<Result<Versioned<ConnectionResponse>>> HandleAsync(CreateConnectionRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ConnectionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (registry.Find(request.Provider) is not { } provider)
        {
            return ConnectionErrors.UnknownProvider;
        }

        var environment = request.Environment.Map<Model.ConnectionEnvironment>();
        if (!provider.Descriptor.Environments.Contains(ConnectionMapper.ToProvider(environment)))
        {
            return ConnectionErrors.UnsupportedEnvironment;
        }

        if (!ProviderRegistry.AllowsInMode(registry.Mode, request.Provider, environment))
        {
            return ConnectionErrors.NotAllowedInMode;
        }

        var credentials = ConnectionFields.Credentials(provider.Descriptor, new Dictionary<string, string>(), request.Credentials);
        if (credentials.IsFailure)
        {
            return credentials.Error;
        }

        var settings = ConnectionFields.Settings(provider.Descriptor, new Dictionary<string, string>(), request.Settings);
        if (settings.IsFailure)
        {
            return settings.Error;
        }

        var created = ProviderConnection.Create(
            request.Key, request.Provider, request.Name, environment, secrets.Encrypt(credentials.Value), settings.Value, request.Enabled, clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (await context.ProviderConnections.AnyAsync(connection => connection.Key == request.Key, cancellationToken))
        {
            return ConnectionErrors.KeyTaken;
        }

        var connection = created.Value;
        context.ProviderConnections.Add(connection);
        idempotency.RecordCreated(ResourceTypes.Connection, connection.Id);
        audit.Record(AuditActions.ConnectionCreated, AuditSubject.Of(AuditSubjects.Connection, connection.Id), new Dictionary<string, object?>
        {
            ["key"] = connection.Key,
            ["provider"] = connection.Provider,
            ["environment"] = request.Environment,
            ["enabled"] = connection.IsEnabled,
            ["credentials"] = credentials.Value.Keys.Order(StringComparer.Ordinal).ToArray(),
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return ConnectionErrors.KeyTaken;
        }

        return new Versioned<ConnectionResponse>(mapper.ToResponse(connection, []), connection.Version);
    }
}

/// <summary>Changes the name, credentials, settings or whether the connection is enabled; credential changes and enabling are audited (PAY-6).</summary>
public sealed class UpdateConnectionHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    ProviderRegistry registry,
    ConnectionSecrets secrets,
    ConnectionMapper mapper,
    BillingClock clock)
{
    public async Task<Result<Versioned<ConnectionResponse>>> HandleAsync(
        string idOrKey, UpdateConnectionRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ConnectionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindConnectionAsync(idOrKey, cancellationToken) is not { } connection)
        {
            return ConnectionErrors.NotFound;
        }

        if (Concurrency.Check(connection, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        if (registry.Find(connection.Provider) is not { } provider)
        {
            return ConnectionErrors.UnknownProvider;
        }

        var settings = ConnectionFields.Settings(provider.Descriptor, connection.Settings, request.Settings);
        if (settings.IsFailure)
        {
            return settings.Error;
        }

        var now = clock.GetUtcNow();
        if (request.Credentials is { Count: > 0 } changes)
        {
            var credentials = ConnectionFields.Credentials(provider.Descriptor, secrets.Decrypt(connection), changes);
            if (credentials.IsFailure)
            {
                return credentials.Error;
            }

            connection.ReplaceCredentials(secrets.Encrypt(credentials.Value), now);
        }

        var wasEnabled = connection.IsEnabled;
        var updated = connection.Update(request.Name ?? connection.Name, settings.Value, request.Enabled ?? connection.IsEnabled, now);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        var data = new Dictionary<string, object?>(Changes.Of(
            ("name", request.Name is not null), ("credentials", request.Credentials is { Count: > 0 }), ("settings", request.Settings is not null), ("enabled", request.Enabled is not null)));
        if (request.Credentials is { Count: > 0 })
        {
            data["credentials"] = request.Credentials.Keys.Order(StringComparer.Ordinal).ToArray();
        }

        if (connection.IsEnabled != wasEnabled)
        {
            data["enabled"] = connection.IsEnabled;
        }

        audit.Record(AuditActions.ConnectionUpdated, AuditSubject.Of(AuditSubjects.Connection, connection.Id), data);
        await context.SaveChangesAsync(cancellationToken);
        return new Versioned<ConnectionResponse>(mapper.ToResponse(connection, await context.RoutedCurrenciesAsync(connection.Id, cancellationToken)), connection.Version);
    }
}

public sealed class GetConnectionHandler(ITahsilDbContext context, IAuthorizer authorizer, ConnectionMapper mapper)
{
    public async Task<Result<Versioned<ConnectionResponse>>> HandleAsync(string idOrKey, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ConnectionsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.FindConnectionAsync(idOrKey, cancellationToken) is { } connection
            ? new Versioned<ConnectionResponse>(mapper.ToResponse(connection, await context.RoutedCurrenciesAsync(connection.Id, cancellationToken)), connection.Version)
            : ConnectionErrors.NotFound;
    }
}

public sealed record ListConnectionsQuery(string? Provider = null, bool? Enabled = null, string? Cursor = null, int? Limit = null);

public sealed class ListConnectionsHandler(ITahsilDbContext context, IAuthorizer authorizer, ConnectionMapper mapper)
{
    private static readonly Keyset<ProviderConnection, Guid> ByCreation = new("created", connection => connection.Id, connection => connection.Id);

    public async Task<Result<Page<ConnectionResponse>>> HandleAsync(ListConnectionsQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ConnectionsRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Paging.IsValidLimit(query.Limit ?? Paging.DefaultLimit))
        {
            return CommonErrors.InvalidLimit();
        }

        var connections = context.ProviderConnections.AsNoTracking();
        if (query.Provider is { } provider)
        {
            connections = connections.Where(connection => connection.Provider == provider);
        }

        if (query.Enabled is { } enabled)
        {
            connections = connections.Where(connection => connection.IsEnabled == enabled);
        }

        var page = await ByCreation.ReadAsync(connections, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        if (page is null)
        {
            return CommonErrors.InvalidCursor;
        }

        var ids = page.Items.Select(connection => connection.Id).ToList();
        var routes = (await context.ConnectionCurrencies.AsNoTracking().Where(route => ids.Contains(route.ConnectionId)).ToListAsync(cancellationToken))
            .ToLookup(route => route.ConnectionId, route => route.Currency);
        return new Page<ConnectionResponse>(
            [.. page.Items.Select(connection => mapper.ToResponse(connection, [.. routes[connection.Id].Order(StringComparer.Ordinal)]))], page.NextCursor);
    }
}

/// <summary>Deletes a connection that never took a payment or saved a card (DOM-9); others are disabled instead.</summary>
public sealed class DeleteConnectionHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IEnumerable<IConnectionUsage> usages)
{
    public async Task<Result> HandleAsync(string idOrKey, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ConnectionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindConnectionAsync(idOrKey, cancellationToken) is not { } connection)
        {
            return ConnectionErrors.NotFound;
        }

        foreach (var usage in usages)
        {
            if (await usage.IsUsedAsync(connection.Id, cancellationToken))
            {
                return ConnectionErrors.InUse;
            }
        }

        var routes = await context.ConnectionCurrencies.Where(route => route.ConnectionId == connection.Id).ToListAsync(cancellationToken);
        context.ConnectionCurrencies.RemoveRange(routes);
        context.ProviderConnections.Remove(connection);
        audit.Record(AuditActions.ConnectionDeleted, AuditSubject.Of(AuditSubjects.Connection, connection.Id), new Dictionary<string, object?>
        {
            ["key"] = connection.Key,
            ["currencies"] = routes.Select(route => route.Currency).Order(StringComparer.Ordinal).ToArray(),
        });
        await context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>Tells whether a connection was used, by one kind of record that uses connections (payments, payment methods).</summary>
public interface IConnectionUsage
{
    Task<bool> IsUsedAsync(Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Runs the adapter's connection test and records its result on the connection (PAY-3).</summary>
public sealed partial class TestConnectionHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    ProviderRegistry registry,
    ProviderAccess providers,
    ConnectionMapper mapper,
    BillingClock clock,
    ILogger<TestConnectionHandler> logger)
{
    public async Task<Result<Versioned<ConnectionResponse>>> HandleAsync(string idOrKey, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.ConnectionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindConnectionAsync(idOrKey, cancellationToken) is not { } connection)
        {
            return ConnectionErrors.NotFound;
        }

        var (status, message) = await RunAsync(connection, cancellationToken);
        connection.RecordTest(status, message, clock.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken);
        return new Versioned<ConnectionResponse>(mapper.ToResponse(connection, await context.RoutedCurrenciesAsync(connection.Id, cancellationToken)), connection.Version);
    }

    private async Task<(Model.ConnectionTestStatus Status, string? Message)> RunAsync(ProviderConnection connection, CancellationToken cancellationToken)
    {
        if (!ProviderRegistry.AllowsInMode(registry.Mode, connection.Provider, connection.Environment))
        {
            return (Model.ConnectionTestStatus.Failed, $"The connection is not allowed in {registry.Mode} mode.");
        }

        if (providers.For(connection) is not { } call)
        {
            return (Model.ConnectionTestStatus.Failed, $"This installation has no '{connection.Provider}' adapter.");
        }

        try
        {
            var result = await call.Provider.TestConnectionAsync(call.Context, cancellationToken);
            var message = (result.FailureCode, result.Message) switch
            {
                ({ } code, { } text) => $"{code}: {text}",
                (var code, var text) => code ?? text,
            };
            return (result.Succeeded ? Model.ConnectionTestStatus.Succeeded : Model.ConnectionTestStatus.Failed, message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAdapterFailed(logger, exception, connection.Key);
            return (Model.ConnectionTestStatus.Failed, "The adapter failed; the server log has the details.");
        }
    }

    [LoggerMessage(LogLevel.Error, "The connection test of {Connection} failed in the adapter")]
    private static partial void LogAdapterFailed(ILogger logger, Exception exception, string connection);
}
