using Microsoft.Extensions.Options;
using Tahsil.Application.Common;

namespace Tahsil.Server.Hosting;

/// <summary>Marks every response of a Test mode installation, so a client can tell it is not moving real money.</summary>
internal static class ModeHeader
{
    public const string Name = "Tahsil-Mode";

    public static IApplicationBuilder UseModeHeader(this IApplicationBuilder app)
    {
        if (!app.ApplicationServices.GetRequiredService<IOptions<ServerOptions>>().Value.IsTestMode)
        {
            return app;
        }

        return app.Use((context, next) =>
        {
            context.Response.Headers[Name] = "test";
            return next(context);
        });
    }
}
