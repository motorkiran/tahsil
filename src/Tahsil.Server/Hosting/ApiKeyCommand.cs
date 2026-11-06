using Tahsil.Application.ApiKeys;
using Tahsil.Contracts;

namespace Tahsil.Server.Hosting;

/// <summary>
/// <c>create-api-key &lt;name&gt; &lt;permission&gt;…</c>: creates an API key without the admin panel, such as for the first
/// application of a new installation or a deployment script. The key is written to the standard output, once; the
/// audit trail records Tahsil itself as its creator.
/// </summary>
internal static class ApiKeyCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string name, IReadOnlyList<string> permissions)
    {
        if (permissions.Count == 0)
        {
            await Console.Error.WriteLineAsync($"Give the key's permissions after its name, such as {TahsilPermissions.CheckoutWrite}. The permissions are: {string.Join(' ', TahsilPermissions.All)}");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<CreateApiKeyHandler>()
            .HandleAsync(new CreateApiKeyRequest { Name = name, Permissions = permissions }, CancellationToken.None);
        if (created.IsFailure)
        {
            await Console.Error.WriteLineAsync($"{created.Error.Code}: {created.Error.Message}");
            return 1;
        }

        Console.WriteLine(created.Value.Key);
        return 0;
    }
}
