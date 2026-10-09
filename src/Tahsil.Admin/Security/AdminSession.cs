using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Tahsil.Application.Common;

namespace Tahsil.Admin.Security;

/// <summary>The claims of an admin session cookie.</summary>
public static class AdminClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "tahsil_role";

    /// <summary>The principal of a panel session.</summary>
    public static ClaimsPrincipal Principal(string subject, string? email, string? name, string role, string authenticationType)
    {
        var claims = new List<Claim> { new(Subject, subject), new(Role, role) };
        if (email is not null)
        {
            claims.Add(new Claim(Email, email));
        }

        if (name is not null)
        {
            claims.Add(new Claim(Name, name));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, Name, Role));
    }
}

/// <summary>The administrator signed in to this circuit: who they are, their role and what it allows.</summary>
public sealed class AdminSession
{
    private string? _subject;

    public bool IsLoaded => _subject is not null;

    public string Subject => _subject ?? throw new InvalidOperationException("The admin session is not loaded yet.");

    public string? Email { get; private set; }

    public string? Name { get; private set; }

    public string Role { get; private set; } = string.Empty;

    public IReadOnlySet<string> Permissions { get; private set; } = new HashSet<string>();

    /// <summary>Where the administrator connects from, for the audit trail.</summary>
    public IPAddress? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public bool Has(string permission) => Permissions.Contains(permission);

    public void Load(ClaimsPrincipal user, IPAddress? ipAddress, string? userAgent)
    {
        if (user.FindFirst(AdminClaims.Subject)?.Value is not { } subject || user.FindFirst(AdminClaims.Role)?.Value is not { } role)
        {
            return;
        }

        _subject = subject;
        Email = user.FindFirst(AdminClaims.Email)?.Value;
        Name = user.FindFirst(AdminClaims.Name)?.Value;
        Role = role;
        Permissions = AdminRoles.PermissionsOf(role);
        IpAddress = ipAddress;
        UserAgent = userAgent;
    }

    /// <summary>The caller that use cases see during this administrator's operations.</summary>
    internal IRequestContext ToRequestContext() => new AdminRequestContext(this);

    private sealed class AdminRequestContext(AdminSession session) : IRequestContext
    {
        public Caller Caller { get; } = new AdminCaller(session.Subject, session.Email, session.Role, session.Permissions);

        public IPAddress? IpAddress => session.IpAddress;

        public string? UserAgent => session.UserAgent;

        public string? CorrelationId => Activity.Current?.TraceId.ToString();
    }
}
