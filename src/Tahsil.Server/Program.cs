using Microsoft.Extensions.Options;
using Tahsil.Admin;
using Tahsil.Application;
using Tahsil.Application.Common;
using Tahsil.Infrastructure;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Security;
using Tahsil.Server.Api;
using Tahsil.Server.Diagnostics;
using Tahsil.Server.ErrorHandling;
using Tahsil.Server.Hosting;
using Tahsil.Server.Simulator;
using Tahsil.Server.Ui;

var builder = WebApplication.CreateBuilder(args);

builder.AddTelemetry();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddErrorHandling();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpRequestContext>();
builder.Services.AddScoped<Tahsil.Application.Checkout.IPageCaller, PageCaller>();
builder.Services.AddScoped<IRequestContext>(services =>
    services.GetRequiredService<RequestContextOverride>().Context ?? services.GetRequiredService<HttpRequestContext>());
builder.Services.AddTahsilAdmin(builder.Configuration);
builder.Services.AddTahsilApi();
builder.Services.AddHostedPages();
builder.Services.AddSimulator();
builder.Services.AddPaymentProviders();
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));
builder.Services.AddHealthProbes();

var app = builder.Build();

// Fail fast on invalid configuration, before anything touches the database.
app.Services.GetRequiredService<IStartupValidator>().Validate();

if (args is ["migrate", ..])
{
    await app.Services.PrepareDatabaseAsync();
    return;
}

// After a master key rotation: rewrites every secret with the current key, so the previous one can be removed.
if (args is ["reencrypt", ..])
{
    await app.Services.ReencryptSecretsAsync();
    return;
}

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
{
    await app.Services.PrepareDatabaseAsync();
}

app.UseErrorResponses();
app.UseHealthProbes();
app.UseSecurityHeaders();

// Each instance can leave the admin panel out, so that only an internal one serves it.
var adminPanel = app.Configuration.IsAdminPanelEnabled();
if (adminPanel)
{
    app.UseAdminContentSecurityPolicy();
}

app.UseModeHeader();

// Once a browser has reached Tahsil over HTTPS, it never tries plain HTTP again. Subdomains are left out: Tahsil may
// run on a domain whose other hosts it does not own.
if (app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.RequireHttps)
{
    app.UseHsts();
}

app.UseRequestLocalization();
app.UseRequestBuffering();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapCultureSwitch();
app.MapCheckoutStatus();
app.MapBrandingStylesheet();
app.MapTahsilApi();
app.MapProviderNotifications();
app.MapContributedEndpoints();

if (adminPanel)
{
    app.MapTahsilAdmin();
}

await app.RunAsync();
