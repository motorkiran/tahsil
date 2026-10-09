using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Pages;

/// <summary>
/// What a person sees when a hosted page fails or does not exist: what happened in plain words, the trace ID to quote
/// to support, and a way back to the page.
/// </summary>
[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorModel : PageModel
{
    public int Status { get; private set; }

    public string TraceId { get; private set; } = string.Empty;

    /// <summary>The page to reload, for errors that a second try may not repeat.</summary>
    public string? RetryUrl { get; private set; }

    public void OnGet(int? status) => Describe(status);

    public void OnPost(int? status) => Describe(status);

    private void Describe(int? status)
    {
        var reExecuted = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        var failed = HttpContext.Features.Get<IExceptionHandlerPathFeature>();

        Status = failed is not null ? StatusCodes.Status500InternalServerError : status is >= 400 and <= 599 ? status.Value : StatusCodes.Status404NotFound;
        Response.StatusCode = Status;
        TraceId = Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier;

        // Re-execution keeps the original method. Only a page that was being viewed is worth reloading: a failed form
        // submission might repeat its action, and a missing page stays missing.
        var originalPath = reExecuted?.OriginalPath ?? failed?.Path;
        if (originalPath is not null && HttpMethods.IsGet(Request.Method) && Status is not (StatusCodes.Status404NotFound or StatusCodes.Status410Gone))
        {
            // The exception handler keeps the query string; status code re-execution replaces it.
            var query = reExecuted is null ? Request.QueryString.Value : reExecuted.OriginalQueryString;
            var url = $"{reExecuted?.OriginalPathBase}{originalPath}{query}";
            RetryUrl = LocalUrls.IsLocal(url) ? url : null;
        }
    }
}
