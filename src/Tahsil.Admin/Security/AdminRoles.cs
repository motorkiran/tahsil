using Tahsil.Contracts;

namespace Tahsil.Admin.Security;

/// <summary>The roles of the admin panel and what each may do (ADM-4).</summary>
public static class AdminRoles
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";

    /// <summary>The roles, most privileged first.</summary>
    public static readonly IReadOnlyList<string> All = [Admin, Operator, Viewer];

    private static readonly string[] OperatorWrites =
    [
        TahsilPermissions.CustomersWrite,
        TahsilPermissions.CheckoutWrite,
        TahsilPermissions.SubscriptionsWrite,
        TahsilPermissions.InvoicesWrite,
        TahsilPermissions.RefundsWrite,
        TahsilPermissions.CouponsWrite,
    ];

    public static IReadOnlySet<string> PermissionsOf(string role) => role switch
    {
        Admin => TahsilPermissions.All.ToHashSet(StringComparer.Ordinal),
        Operator => Reads.Concat(OperatorWrites).ToHashSet(StringComparer.Ordinal),
        Viewer => Reads.ToHashSet(StringComparer.Ordinal),
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    /// <summary>The most privileged of <paramref name="roles"/>, or <see langword="null"/> when none is a panel role.</summary>
    public static string? MostPrivileged(IEnumerable<string> roles)
    {
        var set = roles.ToHashSet(StringComparer.Ordinal);
        return All.FirstOrDefault(set.Contains);
    }

    private static IEnumerable<string> Reads => TahsilPermissions.All.Where(permission => permission.EndsWith(":read", StringComparison.Ordinal));
}
