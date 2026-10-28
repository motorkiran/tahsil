using System.Net;

namespace Tahsil.Client;

/// <summary>The Tahsil API refused a call: its problem details, with the stable <see cref="Code"/> to branch on (SDK-7).</summary>
public sealed class TahsilApiException : Exception
{
    public TahsilApiException()
    {
    }

    public TahsilApiException(string message)
        : base(message)
    {
    }

    public TahsilApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TahsilApiException(
        HttpStatusCode statusCode, string? code, string message, IReadOnlyDictionary<string, string[]>? errors = null, string? traceId = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Errors = errors ?? new Dictionary<string, string[]>();
        TraceId = traceId;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>The problem's <c>code</c>, such as <c>subscription.kimlik_plan_conflict</c>.</summary>
    public string? Code { get; }

    /// <summary>The invalid fields and why, for <c>400</c> answers.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; } = new Dictionary<string, string[]>();

    /// <summary>The trace ID to quote to the installation's operator.</summary>
    public string? TraceId { get; }

    /// <summary>Whether the problem has this <c>code</c>.</summary>
    public bool IsCode(string code) => string.Equals(Code, code, StringComparison.Ordinal);
}
