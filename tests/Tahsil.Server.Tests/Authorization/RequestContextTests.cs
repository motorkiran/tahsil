using Microsoft.AspNetCore.Http;
using Tahsil.Application.Common;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Tests.Authorization;

[Trait("Requirement", "FND-26")]
public sealed class RequestContextTests
{
    [Fact]
    public void OutsideOfARequest_TheSystemIsTheCaller()
    {
        new HttpRequestContext(new HttpContextAccessor()).Caller.ShouldBe(SystemCaller.Instance);
    }

    [Fact]
    public void ARequest_HasTheCallerItsAuthenticationEstablished()
    {
        var context = new DefaultHttpContext();
        var caller = new ApiKeyCaller(Guid.CreateVersion7(), new HashSet<string>());
        context.Features.Set<ICallerFeature>(new CallerFeature(caller));

        new HttpRequestContext(new HttpContextAccessor { HttpContext = context }).Caller.ShouldBe(caller);
    }

    [Fact]
    public void ARequestWithoutACaller_NeverReachesAUseCase()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        Should.Throw<InvalidOperationException>(() => new HttpRequestContext(accessor).Caller);
    }
}
