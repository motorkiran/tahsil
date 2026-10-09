using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Outbox;

/// <summary>
/// Maps the stable names of outbox messages (<see cref="OutboxMessageAttribute"/>) to their types and to the code
/// that deserializes a payload and hands it to the registered handler.
/// </summary>
internal sealed class OutboxMessageTypes
{
    private readonly FrozenDictionary<Type, string> _names;
    private readonly FrozenDictionary<string, Func<IServiceProvider, string, CancellationToken, Task>> _dispatchers;

    public OutboxMessageTypes(IEnumerable<Assembly> assemblies)
    {
        var types = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Select(type => (Type: type, Attribute: type.GetCustomAttribute<OutboxMessageAttribute>()))
            .Where(candidate => candidate.Attribute is not null)
            .ToList();

        _names = types.ToFrozenDictionary(candidate => candidate.Type, candidate => candidate.Attribute!.Type);
        _dispatchers = types.ToFrozenDictionary(
            candidate => candidate.Attribute!.Type,
            candidate => CreateDispatcher(candidate.Type),
            StringComparer.Ordinal);
    }

    public string GetName(Type messageType) => _names.TryGetValue(messageType, out var name)
        ? name
        : throw new InvalidOperationException($"{messageType} is not an outbox message; mark it with [{nameof(OutboxMessageAttribute)}].");

    public Task DispatchAsync(string name, string payload, IServiceProvider services, CancellationToken cancellationToken) =>
        _dispatchers.TryGetValue(name, out var dispatch)
            ? dispatch(services, payload, cancellationToken)
            : throw new InvalidOperationException($"No outbox message type is registered as '{name}'.");

    private static Func<IServiceProvider, string, CancellationToken, Task> CreateDispatcher(Type messageType) =>
        typeof(OutboxMessageTypes)
            .GetMethod(nameof(Dispatch), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(messageType)
            .CreateDelegate<Func<IServiceProvider, string, CancellationToken, Task>>();

    private static Task Dispatch<TMessage>(IServiceProvider services, string payload, CancellationToken cancellationToken)
        where TMessage : class
    {
        var message = JsonSerializer.Deserialize<TMessage>(payload, StorageJson.Options)
            ?? throw new InvalidOperationException($"The outbox payload is not a valid {typeof(TMessage).Name}.");

        return services.GetRequiredService<IOutboxMessageHandler<TMessage>>().HandleAsync(message, cancellationToken);
    }
}
