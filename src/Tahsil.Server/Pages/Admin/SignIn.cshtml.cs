using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Tahsil.Admin.Security;

namespace Tahsil.Server.Pages.Admin;

/// <summary>Where the admin panel sends people without a session: sign in at the provider, or as the developer in development.</summary>
public sealed class SignInModel(IOptions<AdminOptions> options, IAuthenticationSchemeProvider schemes) : PageModel
{
    public bool HasProvider { get; private set; }

    public bool DevelopmentSignIn => options.Value.DevelopmentSignIn;

    public string ReturnUrl { get; private set; } = "/admin";

    public async Task OnGetAsync(string? returnUrl)
    {
        ReturnUrl = AdminAuthentication.LocalOrHome(returnUrl);
        HasProvider = await schemes.GetSchemeAsync(AdminAuthentication.OidcScheme) is not null;
    }
}
