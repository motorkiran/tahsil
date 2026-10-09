using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tahsil.Admin.Security;
using Tahsil.Contracts;

namespace Tahsil.Server.Tests.Admin;

public sealed class AdminSignInRulesTests
{
    private static readonly AdminOidcOptions Kimlik = new()
    {
        RoleMappings = new() { ["tahsil-admin"] = AdminRoles.Admin, ["tahsil-operator"] = AdminRoles.Operator, ["tahsil-viewer"] = AdminRoles.Viewer },
        RoleSource = AdminRoleSource.AccessToken,
        AccessTokenAudience = "tahsil",
    };

    [Theory]
    [Trait("Requirement", "KML-2")]
    [InlineData(new[] { "tahsil-viewer" }, AdminRoles.Viewer)]
    [InlineData(new[] { "tahsil-viewer", "tahsil-admin", "tahsil-operator" }, AdminRoles.Admin)]
    [InlineData(new[] { "other", "tahsil-operator" }, AdminRoles.Operator)]
    public void TheMostPrivilegedMappedRole_Wins(string[] roles, string expected)
    {
        AdminSignInRules.Decide(roles, ["pwd", "mfa"], Kimlik).ShouldBe(AdminSignInDecision.Allow(expected));
    }

    [Theory]
    [Trait("Requirement", "KML-2")]
    [InlineData(new object[] { new string[] { } })]
    [InlineData(new object[] { new[] { "kimlik-admin", "Tahsil-Admin" } })]
    public void NoMappedRole_IsDenied(string[] roles)
    {
        AdminSignInRules.Decide(roles, ["mfa"], Kimlik).Denial.ShouldBe(AdminDenial.NoRole);
    }

    [Fact]
    [Trait("Requirement", "KML-4")]
    public void WithoutASecondFactor_SignInIsDenied_UnlessMfaIsNotRequired()
    {
        AdminSignInRules.Decide(["tahsil-admin"], ["pwd"], Kimlik).Denial.ShouldBe(AdminDenial.SecondFactorRequired);
        AdminSignInRules.Decide(["tahsil-admin"], ["pwd"], new AdminOidcOptions { RoleMappings = Kimlik.RoleMappings, RequireMfa = false })
            .Role.ShouldBe(AdminRoles.Admin);
    }

    [Fact]
    [Trait("Requirement", "ADM-4")]
    public void Roles_GrantTheirPermissions()
    {
        AdminRoles.PermissionsOf(AdminRoles.Admin).Count.ShouldBe(TahsilPermissions.All.Count);
        AdminRoles.PermissionsOf(AdminRoles.Viewer).ShouldAllBe(permission => permission.EndsWith(":read"));
        AdminRoles.PermissionsOf(AdminRoles.Operator).ShouldContain(TahsilPermissions.RefundsWrite);
        AdminRoles.PermissionsOf(AdminRoles.Operator).ShouldNotContain(TahsilPermissions.ApiKeysWrite);
        AdminRoles.PermissionsOf(AdminRoles.Operator).ShouldNotContain(TahsilPermissions.ConnectionsWrite);
        AdminRoles.PermissionsOf("unknown").ShouldBeEmpty();
    }

    [Theory]
    [Trait("Requirement", "KML-3")]
    [InlineData("valid", true)]
    [InlineData("audience", false)]
    [InlineData("issuer", false)]
    [InlineData("type", false)]
    [InlineData("key", false)]
    public async Task AccessTokens_AreReadOnlyWhenTheyValidate(string tamper, bool valid)
    {
        using var rsa = RSA.Create(2048);
        using var stranger = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "kimlik-1" };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = tamper == "issuer" ? "https://evil.example.com/" : "https://id.example.com/",
            Audience = tamper == "audience" ? "kimlik" : "tahsil",
            Subject = new ClaimsIdentity([new Claim("sub", "user-1"), new Claim("roles", "tahsil-admin"), new Claim("roles", "tahsil-viewer")]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            TokenType = tamper == "type" ? "JWT" : "at+jwt",
            SigningCredentials = new SigningCredentials(tamper == "key" ? new RsaSecurityKey(stranger) { KeyId = "kimlik-1" } : key, SecurityAlgorithms.RsaSha256),
        });

        var roles = await AdminAccessTokens.ReadRolesAsync(token, "https://id.example.com/", [key], Kimlik);

        if (valid)
        {
            roles.ShouldBe(["tahsil-admin", "tahsil-viewer"]);
        }
        else
        {
            roles.ShouldBeNull();
        }
    }
}
