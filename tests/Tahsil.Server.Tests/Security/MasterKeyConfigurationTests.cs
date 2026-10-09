using Microsoft.Extensions.Options;

namespace Tahsil.Server.Tests.Security;

[Trait("Requirement", "FND-30")]
public sealed class MasterKeyConfigurationTests
{
    [Theory]
    [InlineData("Tahsil:Security:MasterKeyId", "0", "Tahsil:Security:MasterKeyId must be between 1 and 255.")]
    [InlineData("Tahsil:Security:MasterKeyId", "256", "Tahsil:Security:MasterKeyId must be between 1 and 255.")]
    [InlineData("Tahsil:Security:PreviousMasterKeys:1", "RS4WftirAh28WH2LxhF6vetp9iHyc2xfGSQou5e16R0=", "Tahsil:Security:PreviousMasterKeys must map")]
    [InlineData("Tahsil:Security:PreviousMasterKeys:2", "c2hvcnQ=", "Tahsil:Security:PreviousMasterKeys must map")]
    public void InvalidKeySettings_StopStartup(string key, string value, string message)
    {
        using var host = new TestHost(new Dictionary<string, string?> { [key] = value });

        var failure = Should.Throw<Exception>(host.CreateClient);
        var messages = failure is AggregateException aggregate ? aggregate.InnerExceptions : [failure];

        messages.ShouldContain(exception => exception is OptionsValidationException && exception.Message.Contains(message));
    }
}
