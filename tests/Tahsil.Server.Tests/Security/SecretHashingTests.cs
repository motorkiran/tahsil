using System.Security.Cryptography;
using Tahsil.Application.Common;

namespace Tahsil.Server.Tests.Security;

[Trait("Requirement", "FND-32")]
public sealed class SecretHashingTests
{
    [Fact]
    public void Sha256_IsTheStandardDigest()
    {
        Convert.ToHexStringLower(SecretHashing.Sha256("abc")).ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Hmac_DependsOnTheKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);

        SecretHashing.AreEqual(SecretHashing.Hmac(key, "invoice-42"), SecretHashing.Hmac(key, "invoice-42")).ShouldBeTrue();
        SecretHashing.AreEqual(SecretHashing.Hmac(key, "invoice-42"), SecretHashing.Hmac(RandomNumberGenerator.GetBytes(32), "invoice-42")).ShouldBeFalse();
    }
}
