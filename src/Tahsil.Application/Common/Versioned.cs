namespace Tahsil.Application.Common;

/// <summary>A response with the aggregate's row version, which the API sends as its <c>ETag</c>.</summary>
public sealed record Versioned<T>(T Value, uint Version);
