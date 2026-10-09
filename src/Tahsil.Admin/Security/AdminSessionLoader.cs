using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

namespace Tahsil.Admin.Security;

/// <summary>Loads the circuit's <see cref="AdminSession"/> once, from its authentication state and the request that started it.</summary>
public sealed class AdminSessionLoader(AdminSession session, AuthenticationStateProvider authentication, IHttpContextAccessor httpContextAccessor)
{
    public async Task LoadAsync()
    {
        if (session.IsLoaded)
        {
            return;
        }

        var user = (await authentication.GetAuthenticationStateAsync()).User;
        var request = httpContextAccessor.HttpContext;
        session.Load(user, request?.Connection.RemoteIpAddress, request?.Request.Headers.UserAgent.ToString() is { Length: > 0 } userAgent ? userAgent : null);
    }
}
