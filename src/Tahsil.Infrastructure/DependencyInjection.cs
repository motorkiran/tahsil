using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Security;

namespace Tahsil.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        AddSecurity(services);

        return services;
    }

    private static void AddSecurity(IServiceCollection services)
    {
        services.AddOptions<SecurityOptions>()
            .BindConfiguration(SecurityOptions.SectionName)
            .Validate(
                options => SecurityOptions.IsValidMasterKey(options.MasterKey),
                $"{SecurityOptions.SectionName}:MasterKey must be a base64-encoded 256-bit key. Generate one with 'openssl rand -base64 32'.")
            // Reads the mode from configuration: resolving the server options here would report their own
            // validation failures a second time.
            .Validate<IConfiguration>(
                (options, configuration) => ReadMode(configuration) != TahsilMode.Live || !options.IsDevelopmentMasterKey(),
                $"{SecurityOptions.SectionName}:MasterKey is the public development key, which Live mode refuses. Generate one with 'openssl rand -base64 32'.")
            .ValidateOnStart();
    }

    private static TahsilMode ReadMode(IConfiguration configuration) =>
        configuration.GetSection(ServerOptions.SectionName).GetValue<TahsilMode>(nameof(ServerOptions.Mode));
}
