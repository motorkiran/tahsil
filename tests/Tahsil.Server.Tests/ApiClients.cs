using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.ApiKeys;
using Tahsil.Contracts;
using Tahsil.Contracts.Serialization;

namespace Tahsil.Server.Tests;

/// <summary>Calling the API as an application does: with an API key and Tahsil's JSON conventions.</summary>
internal static class ApiClients
{
    public static readonly JsonSerializerOptions Json = TahsilJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Creates an API key as Tahsil itself (no caller limits) and returns its secret.</summary>
    public static async Task<CreatedApiKeyResponse> CreateApiKeyAsync(this WebApplicationFactory<Program> host, params string[] permissions)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<CreateApiKeyHandler>()
            .HandleAsync(new CreateApiKeyRequest { Name = "Tests", Permissions = permissions }, TestContext.Current.CancellationToken);
        return result.Value;
    }

    public static HttpClient CreateClient(this WebApplicationFactory<Program> host, string apiKey)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    public static async Task<HttpClient> CreateClientWithAsync(this WebApplicationFactory<Program> host, params string[] permissions) =>
        host.CreateClient((await host.CreateApiKeyAsync(permissions)).Key);

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string path, T body) =>
        client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, Json, TestContext.Current.CancellationToken);

    /// <summary>A JSON Merge Patch, guarded by <paramref name="ifMatch"/> when given; send only the fields to change.</summary>
    public static async Task<HttpResponseMessage> PatchJsonAsync<T>(this HttpClient client, string path, T body, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body, options: Json) };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(ifMatch));
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static Task<HttpResponseMessage> PostPathAsync(this HttpClient client, string path) =>
        client.PostAsync(new Uri(path, UriKind.Relative), content: null, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> GetPathAsync(this HttpClient client, string path) =>
        client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, TestContext.Current.CancellationToken))!;

    /// <summary>The <c>code</c> of a problem response.</summary>
    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response) =>
        (await response.ReadAsync<System.Text.Json.Nodes.JsonNode>())["code"]?.GetValue<string>();
}
