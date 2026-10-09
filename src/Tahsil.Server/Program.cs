using Microsoft.Extensions.Options;
using Tahsil.Application;
using Tahsil.Infrastructure;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

// Fail fast on invalid configuration, before anything touches the database.
app.Services.GetRequiredService<IStartupValidator>().Validate();

if (args is ["migrate", ..])
{
    await app.Services.PrepareDatabaseAsync();
    return;
}

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
{
    await app.Services.PrepareDatabaseAsync();
}

app.UseModeHeader();

await app.RunAsync();
