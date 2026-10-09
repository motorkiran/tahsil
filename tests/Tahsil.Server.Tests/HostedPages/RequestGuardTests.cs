using System.Net;
using Microsoft.AspNetCore.Http;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Tests.HostedPages;

[Trait("Requirement", "FND-41")]
public sealed class RequestGuardTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/portal/abc?x=1", true)]
    [InlineData("//evil.example.com", false)]
    [InlineData("/\\evil.example.com", false)]
    [InlineData("/\t/evil.example.com", false)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyPathsOnThisSite_AreLocal(string? url, bool local)
    {
        LocalUrls.IsLocal(url).ShouldBe(local);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    public void Throttling_CountsIPv6ByNetwork(string address, string partition)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        RequestThrottle.PartitionKey(context).ShouldBe(partition);
    }
}
