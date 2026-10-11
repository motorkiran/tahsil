using System.Security.Cryptography;

namespace Tahsil.Server.Tests;

/// <summary>Configuration shared by every in-memory Tahsil host in the tests.</summary>
internal static class TestConfiguration
{
    public const string Environment = "Testing";

    /// <summary>The base address of the in-memory test server.</summary>
    public const string PublicUrl = "http://localhost/";

    /// <summary>A fresh master key per test run; nothing encrypted by the tests outlives it.</summary>
    public static readonly string MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static Dictionary<string, string?> Create(string connectionString) => new()
    {
        ["ConnectionStrings:Tahsil"] = connectionString,
        ["Tahsil:Server:PublicUrl"] = PublicUrl,
        ["Tahsil:Server:RequireHttps"] = "false",
        ["Tahsil:Security:MasterKey"] = MasterKey,
        ["Tahsil:Merchant:TradeName"] = "Acme",
        ["Tahsil:Merchant:LegalName"] = "Acme Yazılım A.Ş.",
        ["Tahsil:Merchant:MersisNumber"] = "0123456789012345",
        ["Tahsil:Merchant:TaxOffice"] = "Kadıköy",
        ["Tahsil:Merchant:TaxNumber"] = "1234567890",
        ["Tahsil:Merchant:Address"] = "Caferağa Mah. Moda Cad. No:1, 34710 Kadıköy/İstanbul",
        ["Tahsil:Merchant:Phone"] = "+90 216 000 00 00",
        ["Tahsil:Merchant:Email"] = "destek@acme.example",
    };
}
