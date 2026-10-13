#:property PublishAot=false

// Sets up Kimlik for Tahsil's development environment, once Kimlik runs (`docker compose --profile kimlik up -d`),
// from the repository's root:
//   dotnet run samples/kimlik/setup.cs
// It creates the billing administrator, billing@tahsil.localhost, with the tahsil-admin role, registers Tahsil's
// webhook endpoint (again, with a new secret, on every run) and puts that secret in Tahsil's user secrets, where the
// `kimlik` launch profile reads it. Kimlik's provisioning file cannot declare users or webhook endpoints.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

const string Email = "billing@tahsil.localhost";
const string Password = "Billing-dev-Passw0rd!";
string[] adminRoles = ["tahsil-admin"];
string[] events = ["user.deleted", "organization.deleted", "subscription.created", "subscription.updated", "subscription.canceled", "subscription.expired"];

var kimlik = new Uri(Environment.GetEnvironmentVariable("KIMLIK_URL") ?? "http://localhost:5280/");

// Kimlik takes plain HTTP for localhost only: in the Compose profile, a relay in Kimlik's container passes its
// localhost:5090 on to Tahsil on the host.
var webhookUrl = Environment.GetEnvironmentVariable("TAHSIL_WEBHOOK_URL") ?? "http://localhost:5090/webhooks/kimlik";
var server = Path.GetFullPath("src/Tahsil.Server");
if (!Directory.Exists(server))
{
    Console.Error.WriteLine("Run it from the repository's root.");
    return 1;
}

using var http = new HttpClient { BaseAddress = kimlik };
using (var token = await http.PostAsync(new Uri("connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "client_credentials",
    ["scope"] = "kimlik",
    ["client_id"] = "tahsil-dev-setup",
    ["client_secret"] = "tahsil-dev-setup-secret",
})))
{
    token.EnsureSuccessStatusCode();
    var accessToken = (await token.Content.ReadFromJsonAsync<JsonObject>())!["access_token"]!.GetValue<string>();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
}

// The billing administrator, created once.
var users = (await http.GetFromJsonAsync<JsonObject>(new Uri($"api/v1/users?q={Uri.EscapeDataString(Email)}", UriKind.Relative)))!["items"]!.AsArray();
var userId = users.FirstOrDefault(user => user!["email"]!.GetValue<string>() == Email)?["id"]!.GetValue<string>();
if (userId is null)
{
    using var created = await http.PostAsJsonAsync(
        new Uri("api/v1/users", UriKind.Relative),
        new { email = Email, givenName = "Billing", familyName = "Administrator", password = Password, emailVerified = true });
    created.EnsureSuccessStatusCode();
    userId = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
}

using (var roles = await http.PutAsJsonAsync(new Uri($"api/v1/users/{userId}/roles", UriKind.Relative), new { roles = adminRoles }))
{
    roles.EnsureSuccessStatusCode();
}

// Tahsil's webhook endpoint, with the events it acts on (KML-22).
var endpoints = (await http.GetFromJsonAsync<JsonObject>(new Uri("api/v1/webhooks/endpoints", UriKind.Relative)))!["items"]!.AsArray();
foreach (var endpoint in endpoints.Where(endpoint => endpoint!["url"]!.GetValue<string>() == webhookUrl))
{
    using var deleted = await http.DeleteAsync(new Uri($"api/v1/webhooks/endpoints/{endpoint!["id"]!.GetValue<string>()}", UriKind.Relative));
    deleted.EnsureSuccessStatusCode();
}

using var registered = await http.PostAsJsonAsync(new Uri("api/v1/webhooks/endpoints", UriKind.Relative), new
{
    url = webhookUrl,
    description = "Tahsil (development)",
    eventTypes = events,
});
if (registered.StatusCode != HttpStatusCode.Created)
{
    Console.Error.WriteLine($"Kimlik refused the webhook endpoint: {await registered.Content.ReadAsStringAsync()}");
    return 1;
}

var secret = (await registered.Content.ReadFromJsonAsync<JsonObject>())!["secret"]!.GetValue<string>();
using var secrets = Process.Start("dotnet", ["user-secrets", "set", "Tahsil:Kimlik:WebhookSecret", secret, "--project", server])!;
await secrets.WaitForExitAsync();

Console.WriteLine($"Kimlik is ready at {kimlik}: sign in to Tahsil's admin panel as {Email} / {Password}.");
Console.WriteLine($"Tahsil's webhook endpoint is {webhookUrl}; its secret is in Tahsil's user secrets.");
return secrets.ExitCode;
