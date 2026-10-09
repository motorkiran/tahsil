using Tahsil.Application.Common;

namespace Tahsil.Server.Tests.Diagnostics;

[Trait("Requirement", "FND-45")]
public sealed class RedactionTests
{
    [Fact]
    public void LogMessages_LoseEmailsAndIdentityNumbers()
    {
        var message = "Card holder ayse.yilmaz@example.com.tr (TCKN 10000000146, VKN 1234567890) was declined; reference 4111111111111111.";

        Redaction.Mask(message).ShouldBe(
            "Card holder a***@example.com.tr (TCKN *********46, VKN ********90) was declined; reference 4111111111111111.");
    }

    [Theory]
    [InlineData("ayse@example.com", "a***@example.com")]
    [InlineData("not-an-email", "***")]
    public void Emails_KeepTheirFirstLetterAndDomain(string email, string masked)
    {
        Redaction.Email(email).ShouldBe(masked);
    }

    [Fact]
    public void NothingToMask_IsLeftAlone()
    {
        Redaction.Mask("Payment 42 failed").ShouldBe("Payment 42 failed");
        Redaction.Mask(null).ShouldBe(string.Empty);
    }
}
