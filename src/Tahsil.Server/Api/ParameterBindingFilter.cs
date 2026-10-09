using Tahsil.Application.Common;
using Tahsil.Domain.Common;

namespace Tahsil.Server.Api;

/// <summary>
/// Stops a request whose route or query values could not be read. Minimal APIs mark such requests with a 400
/// status but, once an endpoint has filters (validation adds one), still run the handler, whose result would
/// replace the status: a mistyped filter would be silently ignored.
/// </summary>
internal sealed class ParameterBindingFilter : IEndpointFilter
{
    private static readonly Error InvalidParameter =
        Error.Validation(CommonErrors.InvalidRequestCode, "A route or query parameter is not valid.");

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Response.StatusCode == StatusCodes.Status400BadRequest
            ? ApiResults.Problem(InvalidParameter)
            : await next(context);
}
