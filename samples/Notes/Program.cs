using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kimlik.AspNetCore;
using Kimlik.AspNetCore.Authorization;
using Kimlik.Client;

var builder = WebApplication.CreateBuilder(args);
var kimlik = builder.Configuration.GetSection("Kimlik");
var tahsil = builder.Configuration.GetSection("Tahsil");
var authority = new Uri(kimlik["Authority"]!);

// Accept Kimlik access tokens issued for this API; the plan in them decides what the caller may do.
builder.Services.AddKimlik(options =>
{
    options.Authority = authority;
    options.Audience = kimlik["Audience"];
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
});

// Reads the plans' features, for RequireFeature, as this API's own service client.
builder.Services.AddKimlikClient(options =>
{
    options.Authority = authority;
    options.ClientId = kimlik["BackendClientId"];
    options.ClientSecret = kimlik["BackendClientSecret"];
});
builder.Services.AddKimlikEntitlements();

// Tahsil's API, with a Test mode API key.
builder.Services.AddHttpClient("tahsil", client =>
{
    client.BaseAddress = new Uri(tahsil["Url"]!);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tahsil["ApiKey"]);
});

builder.Services.AddValidation();
builder.Services.AddSingleton<NoteStore>();

var app = builder.Build();

// The single-page app in wwwroot signs users in with Kimlik and calls the API below with their access token.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/config.json", () => new { Authority = authority, ClientId = kimlik["SpaClientId"] });

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/me", (KimlikUser caller) => new { caller.Name, caller.Email, caller.Plan });

api.MapGet("/notes", (KimlikUser caller, NoteStore notes) => notes.Of(caller.Subject));

api.MapPost("/notes", (NewNote note, KimlikUser caller, NoteStore notes) => notes.Add(caller.Subject, note.Text));

// Only plans with the export_notes feature may export: Pro, which Tahsil sells below.
api.MapGet("/notes/export", (KimlikUser caller, NoteStore notes) =>
        Results.Text(string.Join('\n', notes.Of(caller.Subject).Select(note => note.Text)), "text/plain", Encoding.UTF8))
    .RequireFeature("export_notes");

// Sells Pro through Tahsil's hosted checkout. The Tahsil customer is the Kimlik user, linked to it, so that once the
// first payment succeeds Tahsil gives the user Kimlik's pro plan, and takes it back when the subscription ends.
api.MapPost("/billing/checkout", async (KimlikUser caller, HttpContext http, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    if (caller.UserId is not { } userId)
    {
        return Results.BadRequest();
    }

    var client = clients.CreateClient("tahsil");
    var customer = await CustomerAsync(client, caller, userId, cancellationToken);
    var origin = $"{http.Request.Scheme}://{http.Request.Host}";
    using var session = await client.PostAsJsonAsync(new Uri("api/v1/checkout-sessions", UriKind.Relative), new
    {
        mode = "subscription",
        customer,
        items = new[] { new { price = tahsil["Price"] } },
        successUrl = $"{origin}/?upgraded",
        cancelUrl = $"{origin}/",
    }, cancellationToken);

    // subscription.kimlik_plan_conflict: the customer already has a subscription with a Kimlik plan.
    return session.IsSuccessStatusCode
        ? Results.Ok(new { Url = (await session.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!["url"]!.GetValue<string>() })
        : Results.Problem(await session.Content.ReadAsStringAsync(cancellationToken), statusCode: (int)session.StatusCode);
});

// Tahsil's customer portal: the plan, cards, invoices and cancellation.
api.MapPost("/billing/portal", async (KimlikUser caller, HttpContext http, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    if (caller.UserId is not { } userId)
    {
        return Results.BadRequest();
    }

    var client = clients.CreateClient("tahsil");
    var customer = await CustomerAsync(client, caller, userId, cancellationToken);
    using var session = await client.PostAsJsonAsync(
        new Uri("api/v1/portal-sessions", UriKind.Relative),
        new { customer, returnUrl = $"{http.Request.Scheme}://{http.Request.Host}/" },
        cancellationToken);
    session.EnsureSuccessStatusCode();
    return Results.Ok(new { Url = (await session.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!["url"]!.GetValue<string>() });
});

await app.RunAsync();

// The Tahsil customer of a Kimlik user, created or updated by the user's ID as its external ID (CUS-2).
static async Task<Guid> CustomerAsync(HttpClient client, KimlikUser caller, Guid userId, CancellationToken cancellationToken)
{
    using var response = await client.PutAsJsonAsync(new Uri($"api/v1/customers/by-external-id/{userId}", UriKind.Relative), new
    {
        email = caller.Email,
        name = caller.Name,
        kimlikSubscriber = new { type = "user", id = userId },
    }, cancellationToken);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!["id"]!.GetValue<Guid>();
}

internal sealed record Note(Guid Id, string Text, DateTimeOffset CreatedAt);

internal sealed record NewNote([property: Required, StringLength(500)] string Text);

/// <summary>Notes kept in memory, by the Kimlik subject; a real app would use a database.</summary>
internal sealed class NoteStore
{
    private readonly Lock _lock = new();
    private readonly List<(string Owner, Note Note)> _notes = [];

    public IReadOnlyList<Note> Of(string owner)
    {
        lock (_lock)
        {
            return [.. _notes.Where(entry => entry.Owner == owner).Select(entry => entry.Note)];
        }
    }

    public Note Add(string owner, string text)
    {
        var note = new Note(Guid.CreateVersion7(), text.Trim(), DateTimeOffset.UtcNow);
        lock (_lock)
        {
            _notes.Add((owner, note));
        }

        return note;
    }
}
