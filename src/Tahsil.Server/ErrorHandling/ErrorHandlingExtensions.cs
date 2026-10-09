using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Server.Api;
using Tahsil.Server.Ui;

namespace Tahsil.Server.ErrorHandling;

internal static partial class ErrorHandlingExtensions
{
    public const string TraceIdExtension = "traceId";

    public static IServiceCollection AddErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            problem.Extensions[TraceIdExtension] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;

            // Malformed requests get a code too, like every other API error.
            if (problem.Status == StatusCodes.Status400BadRequest)
            {
                problem.Extensions.TryAdd(ApiResults.CodeExtension, CommonErrors.InvalidRequestCode);
            }

            if (problem is ServerErrorProblemDetails { Error: { } error })
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ErrorHandlingExtensions));
                LogFailure(logger, error.Code, error.Message, (string)problem.Extensions[TraceIdExtension]!);
            }
        });
        services.AddExceptionHandler<UnhandledExceptionHandler>();

        // The handler logs every exception itself, with the trace ID; the middleware would log it a second time.
        services.Configure<ExceptionHandlerOptions>(options => options.SuppressDiagnosticsCallback = _ => true);

        return services;
    }

    [LoggerMessage(LogLevel.Error, "Request failed with {Code}: {Message} (trace {TraceId})")]
    private static partial void LogFailure(ILogger logger, string code, string message, string traceId);

    /// <summary>
    /// Answers unhandled exceptions with a generic problem and logs them with the trace ID. A write that lost an
    /// optimistic concurrency race is a conflict, not a server error.
    /// </summary>
    private sealed partial class UnhandledExceptionHandler(IProblemDetailsService problemDetails, ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

            // People get the localized error page, which shows the trace ID.
            if (HostedPages.IsPageRequest(httpContext))
            {
                LogUnhandled(logger, exception, traceId);
                return false;
            }

            Microsoft.AspNetCore.Mvc.ProblemDetails problem;
            if (exception is DbUpdateConcurrencyException)
            {
                problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Detail = CommonErrors.ConcurrencyConflict.Message,
                };
                problem.Extensions[ApiResults.CodeExtension] = CommonErrors.ConcurrencyConflict.Code;
            }
            else
            {
                LogUnhandled(logger, exception, traceId);
                problem = new ServerErrorProblemDetails();
            }

            httpContext.Response.StatusCode = problem.Status!.Value;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problem,
                Exception = exception,
            });
        }

        [LoggerMessage(LogLevel.Error, "Unhandled exception (trace {TraceId})")]
        private static partial void LogUnhandled(ILogger logger, Exception exception, string traceId);
    }
}
