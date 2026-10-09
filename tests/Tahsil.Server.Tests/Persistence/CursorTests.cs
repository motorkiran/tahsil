using Tahsil.Application.Common;

namespace Tahsil.Server.Tests.Persistence;

[Trait("Requirement", "FND-21")]
public sealed class CursorTests
{
    [Fact]
    public void RoundTrips_TheSortKeyAndId()
    {
        var id = Guid.CreateVersion7();
        var at = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.FromHours(3));
        var encoded = new Cursor("newest", Cursor.FormatKey(at), id).Encode();

        Cursor.TryDecode(encoded, "newest", out var cursor).ShouldBeTrue();
        Cursor.TryParseKey<DateTimeOffset>(cursor.Key, out var key).ShouldBeTrue();

        key.ShouldBe(at);
        cursor.Id.ShouldBe(id);
        encoded.ShouldAllBe(character => char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_');
    }

    [Fact]
    public void StringKeys_MayContainTheSeparator()
    {
        var encoded = new Cursor("name", "a\nb", Guid.Empty).Encode();

        Cursor.TryDecode(encoded, "name", out var cursor).ShouldBeTrue();
        cursor.Key.ShouldBe("a\nb");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("bmV3ZXN0")]
    public void RejectsMalformedCursors(string? value)
    {
        Cursor.TryDecode(value, "newest", out _).ShouldBeFalse();
    }
}
