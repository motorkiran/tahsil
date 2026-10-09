using Microsoft.Extensions.Options;
using Tahsil.Infrastructure.Security;

namespace Tahsil.Server.Tests.Hosting;

public sealed class ConfigurationTests
{
    public static TheoryData<string, string?, string> InvalidSettings => new()
    {
        { "Tahsil:Server:PublicUrl", null, "Tahsil:Server:PublicUrl must be an absolute" },
        { "Tahsil:Server:PublicUrl", "/billing/", "Tahsil:Server:PublicUrl must be an absolute" },
        { "Tahsil:Server:PublicUrl", "ftp://billing.example.com/", "Tahsil:Server:PublicUrl must be an absolute" },
        { "Tahsil:Server:PublicUrl", "https://billing.example.com/?tenant=1", "Tahsil:Server:PublicUrl must be an absolute" },
        { "Tahsil:Server:DefaultLocale", "de", "Tahsil:Server:DefaultLocale must be one of" },
        { "Tahsil:Security:MasterKey", null, "Tahsil:Security:MasterKey must be a base64-encoded 256-bit key" },
        { "Tahsil:Security:MasterKey", "c2hvcnQ=", "Tahsil:Security:MasterKey must be a base64-encoded 256-bit key" },
    };

    [Theory]
    [Trait("Requirement", "FND-8")]
    [MemberData(nameof(InvalidSettings))]
    public void InvalidSettings_StopStartup_NamingTheSetting(string key, string? value, string message)
    {
        using var host = new TestHost(new Dictionary<string, string?> { [key] = value });

        StartupFailure(host).ShouldContain(message);
    }

    [Fact]
    [Trait("Requirement", "FND-8")]
    public void PlainHttp_RequiresRequireHttpsToBeOff()
    {
        using var host = new TestHost(new Dictionary<string, string?> { ["Tahsil:Server:RequireHttps"] = "true" });

        StartupFailure(host).ShouldContain("Tahsil:Server:PublicUrl must use HTTPS unless Tahsil:Server:RequireHttps is false.");
    }

    [Fact]
    [Trait("Requirement", "FND-10")]
    public void LiveMode_RefusesToStart_WithoutRequireHttps()
    {
        using var host = new TestHost(new Dictionary<string, string?>
        {
            ["Tahsil:Server:Mode"] = "Live",
            ["Tahsil:Server:PublicUrl"] = "https://billing.example.com/",
        });

        StartupFailure(host).ShouldContain("Tahsil:Server:RequireHttps cannot be false in Live mode.");
    }

    [Fact]
    [Trait("Requirement", "FND-10")]
    public void LiveMode_RefusesToStart_WithTheDevelopmentMasterKey()
    {
        using var host = new TestHost(new Dictionary<string, string?>
        {
            ["Tahsil:Server:Mode"] = "Live",
            ["Tahsil:Server:PublicUrl"] = "https://billing.example.com/",
            ["Tahsil:Server:RequireHttps"] = "true",
            ["Tahsil:Security:MasterKey"] = SecurityOptions.DevelopmentMasterKey,
        });

        StartupFailure(host).ShouldContain("Tahsil:Security:MasterKey is the public development key");
    }

    [Fact]
    [Trait("Requirement", "FND-10")]
    public void TestMode_AcceptsTheDevelopmentMasterKey()
    {
        using var host = new TestHost(new Dictionary<string, string?> { ["Tahsil:Security:MasterKey"] = SecurityOptions.DevelopmentMasterKey });

        Should.NotThrow(host.CreateClient).Dispose();
    }

    /// <summary>The validation messages that stopped the host, one or several.</summary>
    private static string StartupFailure(TestHost host)
    {
        var failure = Should.Throw<Exception>(host.CreateClient);
        var failures = failure is AggregateException aggregate ? aggregate.InnerExceptions : [failure];

        failures.ShouldAllBe(exception => exception is OptionsValidationException);
        return string.Join(Environment.NewLine, failures.Select(exception => exception.Message));
    }
}
