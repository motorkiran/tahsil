using Microsoft.Extensions.Options;
using Tahsil.Application;
using Tahsil.Infrastructure;
using Tahsil.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

// Fail fast on invalid configuration, before anything touches the database.
app.Services.GetRequiredService<IStartupValidator>().Validate();

app.UseModeHeader();

await app.RunAsync();
