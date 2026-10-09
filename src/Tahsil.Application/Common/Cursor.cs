using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Tahsil.Application.Common;

/// <summary>
/// The position after the last item of a page: its sort key and its ID, which breaks ties. A cursor is bound to the
/// sort it was made for, so it cannot be replayed against another list order.
/// </summary>
/// <remarks>
/// Cursors are opaque but not secret: a caller who edits one only moves within a list it may read anyway, so they
/// are not signed. Anything that does not decode for the expected sort is rejected.
/// </remarks>
public sealed record Cursor(string Sort, string Key, Guid Id)
{
    private const char Separator = '\n';

    public string Encode() => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{Sort}{Separator}{Key}{Separator}{Id:N}"));

    /// <summary>Decodes a cursor that was made for <paramref name="sort"/>.</summary>
    public static bool TryDecode(string? value, string sort, out Cursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value) || value.Length > 1024)
        {
            return false;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
        }
        catch (FormatException)
        {
            return false;
        }

        var first = text.IndexOf(Separator, StringComparison.Ordinal);
        var last = text.LastIndexOf(Separator);
        if (first <= 0 || last == first
            || !string.Equals(text[..first], sort, StringComparison.Ordinal)
            || !Guid.TryParseExact(text[(last + 1)..], "N", out var id))
        {
            return false;
        }

        cursor = new Cursor(sort, text[(first + 1)..last], id);
        return true;
    }

    /// <summary>The text form of a sort key that round-trips exactly.</summary>
    public static string FormatKey<TKey>(TKey key) => key switch
    {
        DateTimeOffset time => time.UtcTicks.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        string text => text,
        Guid id => id.ToString("N"),
        _ => throw new NotSupportedException($"Cursors do not support sort keys of type {typeof(TKey)}."),
    };

    public static bool TryParseKey<TKey>(string text, out TKey key)
    {
        object? parsed = typeof(TKey) switch
        {
            var type when type == typeof(DateTimeOffset) =>
                long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks <= DateTimeOffset.MaxValue.UtcTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null,
            var type when type == typeof(long) =>
                long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null,
            var type when type == typeof(int) =>
                int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null,
            var type when type == typeof(string) => text,
            var type when type == typeof(Guid) => Guid.TryParseExact(text, "N", out var id) ? id : null,
            _ => throw new NotSupportedException($"Cursors do not support sort keys of type {typeof(TKey)}."),
        };

        key = parsed is TKey value ? value : default!;
        return parsed is not null;
    }
}
