using Microsoft.AspNetCore.Http.HttpResults;
using Tahsil.Domain.Common;
using Tahsil.Server.ErrorHandling;

namespace Tahsil.Server.Api;

/// <summary>Turns use case results into HTTP responses; failures become RFC 9457 problem details with a stable <c>code</c>.</summary>
internal static class ApiResults
{
    /// <summary>The problem details member carrying the machine-readable error code.</summary>
    public const string CodeExtension = "code";

    public static Results<Ok<T>, ProblemHttpResult> ToOk<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : Problem(result.Error);

    public static Results<Created<T>, ProblemHttpResult> ToCreated<T>(this Result<T> result, Func<T, string> location) =>
        result.IsSuccess ? TypedResults.Created(location(result.Value), result.Value) : Problem(result.Error);

    public static Results<NoContent, ProblemHttpResult> ToNoContent(this Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : Problem(result.Error);

    public static ProblemHttpResult Problem(Error error)
    {
        if (error.Type == ErrorType.Failure)
        {
            return TypedResults.Problem(new ServerErrorProblemDetails(error));
        }

        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCode(error.Type),
            Detail = error.Message,
        };
        problem.Extensions[CodeExtension] = error.Code;
        return TypedResults.Problem(problem);
    }

    public static int StatusCode(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorType.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };
}
