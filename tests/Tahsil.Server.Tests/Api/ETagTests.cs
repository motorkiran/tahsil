using Microsoft.AspNetCore.Http;
using Tahsil.Application.Common;
using Tahsil.Server.Api;
using Tahsil.Server.Tests.Persistence;

namespace Tahsil.Server.Tests.Api;

[Trait("Requirement", "FND-14")]
public sealed class ETagTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("*", null)]
    [InlineData("\"5\"", new uint[] { 5 })]
    [InlineData("\"5\", \"7\"", new uint[] { 5, 7 })]
    public void IfMatch_IsReadAsTheAcceptedVersions(string? header, uint[]? expected)
    {
        var result = Request(header).ReadIfMatch();

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("W/\"5\"")]
    [InlineData("5")]
    [InlineData("\"five\"")]
    [InlineData("\"-1\"")]
    public void IfMatch_ThatIsNotATahsilETag_IsAnInvalidRequest(string header)
    {
        Request(header).ReadIfMatch().Error.ShouldNotBeNull().Code.ShouldBe("request.invalid");
    }

    [Fact]
    public void Versions_AreFormattedAsStrongETags()
    {
        ETags.Format(42).ShouldBe("\"42\"");
    }

    [Fact]
    public void TheCheck_PassesWithoutAVersion_OrWithTheCurrentOne()
    {
        var aggregate = new TestAggregate { Id = Guid.CreateVersion7(), Name = "Version 0" };

        Concurrency.Check(aggregate, null).IsSuccess.ShouldBeTrue();
        Concurrency.Check(aggregate, [0u]).IsSuccess.ShouldBeTrue();
        Concurrency.Check(aggregate, [3u]).Error.ShouldBe(CommonErrors.PreconditionFailed);
    }

    private static HttpRequest Request(string? ifMatch)
    {
        var context = new DefaultHttpContext();
        if (ifMatch is not null)
        {
            context.Request.Headers.IfMatch = ifMatch;
        }

        return context.Request;
    }
}
