using Tahsil.Application.Checkout;
using Tahsil.Application.Common;

namespace Tahsil.Server.Hosting;

/// <summary>Makes the customer of a hosted page the caller of the request once the page knows them.</summary>
internal sealed class PageCaller(IHttpContextAccessor accessor) : IPageCaller
{
    public void Use(Caller caller) =>
        (accessor.HttpContext ?? throw new InvalidOperationException("Hosted pages act within a request.")).Features.Set<ICallerFeature>(new CallerFeature(caller));
}
