using Microsoft.Extensions.Options;
using Tahsil.Application;
using Tahsil.Infrastructure;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Security;
using Tahsil.Server.Api;
using Tahsil.Server.ErrorHandling;
using Tahsil.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddErrorHandling();
builder.Services.AddTahsilApi();

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

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseModeHeader();

app.MapTahsilApi();

await app.RunAsync();
