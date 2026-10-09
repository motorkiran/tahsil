using Microsoft.Extensions.Options;
using Tahsil.Application.Common;

namespace Tahsil.Server.Tests.Hosting;

[Trait("Requirement", "FND-11")]
public sealed class PublicUrlsTests
{
    [Theory]
    [InlineData("https://billing.example.com/", "https://billing.example.com/checkout/abc")]
    [InlineData("https://example.com/billing", "https://example.com/billing/checkout/abc")]
    [InlineData("https://example.com/billing/", "https://example.com/billing/checkout/abc")]
    public void BuildsUnderThePublicUrl(string publicUrl, string expected)
    {
        Urls(publicUrl).Build("checkout/abc").ShouldBe(new Uri(expected));
    }

    [Fact]
    public void EscapesQueryValues_AndSkipsMissingOnes()
    {
        var url = Urls("https://billing.example.com/").Build("portal", ("returnUrl", "https://app.example.com/a?b=c&d"), ("locale", null), ("x", "ş"));

        url.AbsoluteUri.ShouldBe("https://billing.example.com/portal?returnUrl=https%3A%2F%2Fapp.example.com%2Fa%3Fb%3Dc%26d&x=%C5%9F");
    }

    [Theory]
    [InlineData("/checkout")]
    [InlineData("//evil.example.com/x")]
    [InlineData("https://evil.example.com/")]
    public void RejectsPathsThatLeaveThePublicUrl(string path)
    {
        Should.Throw<ArgumentException>(() => Urls("https://billing.example.com/").Build(path));
    }

    private static PublicUrls Urls(string publicUrl) => new(Options.Create(new ServerOptions { PublicUrl = new Uri(publicUrl) }));
}
