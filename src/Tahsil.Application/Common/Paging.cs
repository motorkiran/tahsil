namespace Tahsil.Application.Common;

/// <summary>The page size rules of every list.</summary>
public static class Paging
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    public static bool IsValidLimit(int limit) => limit is >= 1 and <= MaxLimit;
}
