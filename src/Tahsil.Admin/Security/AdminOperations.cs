using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Common;

namespace Tahsil.Admin.Security;

/// <summary>
/// Runs Application handlers for the admin panel, each operation in a scope of its own (a circuit lasts long, a
/// database context should not), as the signed-in administrator. The handlers check the administrator's permissions
/// themselves and answer with errors, as they do for API keys, so the panel enforces exactly the API's rules.
/// </summary>
public sealed class AdminOperations(IServiceScopeFactory scopeFactory, AdminSession session)
{
    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> operation)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestContextOverride>().Context = session.ToRequestContext();
        return await operation(scope.ServiceProvider);
    }

    /// <summary>Runs <typeparamref name="THandler"/>, resolved in the operation's scope.</summary>
    public Task<T> RunAsync<THandler, T>(Func<THandler, Task<T>> operation)
        where THandler : notnull =>
        RunAsync(services => operation(services.GetRequiredService<THandler>()));
}
