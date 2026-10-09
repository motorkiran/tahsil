using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Security;

namespace Tahsil.Server.Tests.Security;

public sealed class SecretProtectorTests
{
    private static readonly string Key1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string Key2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("sk_test_51H…");

    public static TheoryData<string> Purposes =>
    [
        SecretPurposes.ConnectionCredentials,
        SecretPurposes.PaymentMethodToken,
        SecretPurposes.WebhookEndpointSecret,
        SecretPurposes.ProviderNotificationRawBody,
        SecretPurposes.CustomerTaxId,
        SecretPurposes.InvoiceLinkKey,
        SecretPurposes.DataProtectionKeyRing,
    ];

    [Theory]
    [Trait("Requirement", "FND-31")]
    [MemberData(nameof(Purposes))]
    public void RoundTrips_PerPurpose(string purpose)
    {
        var encryption = new SecretEncryption(Protector(Key1));
        var owner = Guid.CreateVersion7();

        var encrypted = encryption.Encrypt("4111-token", purpose, owner);

        encryption.DecryptString(encrypted, purpose, owner).ShouldBe("4111-token");
    }

    [Fact]
    [Trait("Requirement", "FND-29")]
    public void Ciphertext_MovedToAnotherRowOrPurpose_DoesNotDecrypt()
    {
        var encryption = new SecretEncryption(Protector(Key1));
        var owner = Guid.CreateVersion7();
        var encrypted = encryption.Encrypt(Secret, SecretPurposes.ConnectionCredentials, owner);

        Should.Throw<CryptographicException>(() => encryption.Decrypt(encrypted, SecretPurposes.ConnectionCredentials, Guid.CreateVersion7()));
        Should.Throw<CryptographicException>(() => encryption.Decrypt(encrypted, SecretPurposes.PaymentMethodToken, owner));
        Should.Throw<CryptographicException>(() => encryption.Decrypt(encrypted, SecretPurposes.ConnectionCredentials));
    }

    [Fact]
    [Trait("Requirement", "FND-29")]
    public void TamperedCiphertext_DoesNotDecrypt()
    {
        var protector = Protector(Key1);
        var encrypted = protector.Protect(Secret, SecretPurposes.ConnectionCredentials);
        encrypted[^1] ^= 1;

        Should.Throw<CryptographicException>(() => protector.Unprotect(encrypted, SecretPurposes.ConnectionCredentials));
        Should.Throw<CryptographicException>(() => protector.Unprotect(encrypted.AsSpan(0, 20), SecretPurposes.ConnectionCredentials));
    }

    [Fact]
    [Trait("Requirement", "FND-29")]
    public void Output_StartsWithTheFormatVersion_AndTheMasterKeyId()
    {
        var encrypted = Protector(Key1, keyId: 7).Protect(Secret, SecretPurposes.ConnectionCredentials);

        encrypted[0].ShouldBe((byte)2);
        encrypted[1].ShouldBe((byte)7);
        encrypted.Length.ShouldBe(2 + 12 + Secret.Length + 16);
    }

    [Fact]
    [Trait("Requirement", "FND-30")]
    public void PreviousMasterKeys_StillDecrypt_WhatTheyEncrypted()
    {
        var before = Protector(Key1, keyId: 1);
        var after = Protector(Key2, keyId: 2, previous: new() { [1] = Key1 });
        var encrypted = before.Protect(Secret, SecretPurposes.ConnectionCredentials);

        after.Unprotect(encrypted, SecretPurposes.ConnectionCredentials).ShouldBe(Secret);
        after.IsCurrent(encrypted).ShouldBeFalse();
        after.IsCurrent(after.Protect(Secret, SecretPurposes.ConnectionCredentials)).ShouldBeTrue();
    }

    [Fact]
    [Trait("Requirement", "FND-30")]
    public void AMissingMasterKey_IsNamedInTheError()
    {
        var encrypted = Protector(Key1, keyId: 1).Protect(Secret, SecretPurposes.ConnectionCredentials);

        Should.Throw<CryptographicException>(() => Protector(Key2, keyId: 2).Unprotect(encrypted, SecretPurposes.ConnectionCredentials))
            .Message.ShouldContain("Tahsil:Security:PreviousMasterKeys:1");
    }

    private static SecretProtector Protector(string key, int keyId = 1, Dictionary<int, string>? previous = null) =>
        new(Options.Create(new SecurityOptions { MasterKey = key, MasterKeyId = keyId, PreviousMasterKeys = previous ?? [] }));
}
