using Microsoft.Extensions.DependencyInjection;

namespace Tahsil.Infrastructure.Persistence;

public static class DatabasePreparationExtensions
{
    /// <inheritdoc cref="DatabasePreparation"/>
    public static async Task PrepareDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabasePreparation>().RunAsync(cancellationToken);
    }
}
