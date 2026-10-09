using System.Text.Json.Serialization;
using Tahsil.Domain.Common;

namespace Tahsil.Server.ErrorHandling;

/// <summary>
/// The answer to something that should not happen: a generic message and the trace ID to quote. The internal error
/// is kept for the log and never serialized.
/// </summary>
internal sealed class ServerErrorProblemDetails : Microsoft.AspNetCore.Mvc.ProblemDetails
{
    public const string Code = "server.error";

    public const string GenericDetail = "Something went wrong; quote the trace ID.";

    public ServerErrorProblemDetails(Error? error = null)
    {
        Error = error;
        Status = StatusCodes.Status500InternalServerError;
        Detail = GenericDetail;
        Extensions[Api.ApiResults.CodeExtension] = Code;
    }

    [JsonIgnore]
    public Error? Error { get; }
}
