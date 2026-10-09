using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Tahsil.Admin.Security;

namespace Tahsil.Server.Pages.Admin;

/// <summary>Why the admin panel turned someone away, and what they can do about it.</summary>
public sealed class DeniedModel(IOptions<AdminOptions> options) : PageModel
{
    public string Reason { get; private set; } = "role";

    public Uri? AccountUrl => options.Value.Oidc.AccountUrl;

    public void OnGet(string? reason) => Reason = reason is "mfa" or "token" ? reason : "role";
}
