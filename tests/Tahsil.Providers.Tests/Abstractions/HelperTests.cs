using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Tests.Abstractions;

public sealed class HelperTests
{
    [Theory]
    [Trait("Requirement", "ADP-8")]
    [InlineData(1250, 2, "12.50", "12.5")]
    [InlineData(1200, 2, "12.00", "12")]
    [InlineData(5, 2, "0.05", "0.05")]
    [InlineData(-199, 2, "-1.99", "-1.99")]
    [InlineData(1000, 0, "1000", "1000")]
    [InlineData(123456, 3, "123.456", "123.456")]
    [InlineData(long.MaxValue, 2, "92233720368547758.07", "92233720368547758.07")]
    public void Amounts_ConvertExactly(long minorUnits, int exponent, string fixedDigits, string shortest)
    {
        var amount = new Amount(minorUnits, "TRY", exponent);

        AmountFormat.Fixed(amount).ShouldBe(fixedDigits);
        AmountFormat.Shortest(amount).ShouldBe(shortest);
        AmountFormat.ToMinorUnits(fixedDigits, exponent).ShouldBe(minorUnits);
        AmountFormat.ToMinorUnits(shortest, exponent).ShouldBe(minorUnits);
    }

    [Theory]
    [Trait("Requirement", "ADP-8")]
    [InlineData("12.505", 2)]
    [InlineData("1,5", 2)]
    [InlineData("abc", 2)]
    [InlineData(null, 2)]
    [InlineData("0.5", 0)]
    public void AmountsWithMoreDigitsThanTheCurrency_AreNotRead(string? value, int exponent)
    {
        AmountFormat.ToMinorUnits(value, exponent).ShouldBeNull();
    }

    [Fact]
    public void Hmac_MatchesKnownVectors_AndComparesInConstantTime()
    {
        // RFC 4231 test case 2.
        Hmac.Hex(HmacAlgorithm.Sha256, "Jefe", "what do ya want for nothing?")
            .ShouldBe("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843");
        Hmac.Base64(HmacAlgorithm.Sha1, "key", "The quick brown fox jumps over the lazy dog").ShouldBe("3nybhbi3iqa8ino29wqQcBydtNk=");

        var expected = Hmac.Compute(HmacAlgorithm.Sha256, "Jefe", "what do ya want for nothing?");
        Hmac.MatchesHex(expected, "5BDCC146BF60754E6A042426089575C75A003F089D2739839DEC58B964EC3843").ShouldBeTrue();
        Hmac.MatchesHex(expected, "5bdcc146").ShouldBeFalse();
        Hmac.MatchesHex(expected, new string('z', 64)).ShouldBeFalse();
        Hmac.MatchesHex(expected, null).ShouldBeFalse();
        Hmac.Matches("abc", "abc").ShouldBeTrue();
        Hmac.Matches("abc", "abd").ShouldBeFalse();
        Hmac.Matches("abc", null).ShouldBeFalse();
    }

    [Fact]
    public void Forms_EncodeInOrder_AndDecode()
    {
        var encoded = FormEncoding.Encode([new("merchant_oid", "abc123"), new("email", "ayşe+test@example.com"), new("note", "a b&c=d")]);

        encoded.ShouldBe("merchant_oid=abc123&email=ay%C5%9Fe%2Btest%40example.com&note=a%20b%26c%3Dd");
        FormEncoding.Decode(encoded).ShouldBe(new Dictionary<string, string>
        {
            ["merchant_oid"] = "abc123",
            ["email"] = "ayşe+test@example.com",
            ["note"] = "a b&c=d",
        });
        FormEncoding.Decode("?status=success&hash=a+b&flag").ShouldBe(new Dictionary<string, string> { ["status"] = "success", ["hash"] = "a b", ["flag"] = "" });
    }

    [Fact]
    public void LenientJson_ReadsLooselyTypedValues()
    {
        using var document = LenientJson.TryParse("""
            {
              "Status": "success", "paidPrice": "12.50", "price": 12.5, "fraudStatus": "1", "installment": 1,
              "threeDS": "false", "itemTransactions": [ { "id": 1 }, { "id": 2 } ], "card": { "lastFour": 4242 },
              "createdAt": "2026-10-10T12:00:00Z", "created": 1791633600, "missing": null
            }
            """)!;
        var root = document.RootElement;

        root.TextAt("status").ShouldBe("success");
        root.DecimalAt("paidPrice").ShouldBe(12.50m);
        root.DecimalAt("price").ShouldBe(12.5m);
        root.Int64At("fraudStatus").ShouldBe(1);
        root.BooleanAt("installment").ShouldBe(true);
        root.BooleanAt("threeDS").ShouldBe(false);
        root.ArrayAt("itemTransactions").Select(item => item.Int64At("id")).ShouldBe([1L, 2L]);
        root.TextAt("card", "lastFour").ShouldBe("4242");
        root.DateTimeAt("createdAt").ShouldBe(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        root.DateTimeAt("created").ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1791633600));
        root.TextAt("missing").ShouldBeNull();
        root.TextAt("card", "nothing").ShouldBeNull();
        root.Int64At("status").ShouldBeNull();
        root.ArrayAt("card").ShouldBeEmpty();
        LenientJson.TryParse("<html>502</html>").ShouldBeNull();
        LenientJson.TryParse("").ShouldBeNull();
    }

    [Fact]
    [Trait("Requirement", "ADP-9")]
    public void Redaction_MasksSecretsAndPersonalData_AtAnyDepth()
    {
        var json = Redaction.Body(
            """{"apiKey":"k","price":"1.0","buyer":{"name":"Ayşe","Email":"a@b.c","identityNumber":"11111111111"},"items":[{"token":"t","id":"1"}]}""",
            "application/json");

        json.ShouldBe("""{"apiKey":"***","price":"1.0","buyer":{"name":"***","Email":"***","identityNumber":"***"},"items":[{"token":"***","id":"1"}]}""");
        Redaction.Body("merchant_id=1&merchant_key=secret&user_phone=555", "application/x-www-form-urlencoded")
            .ShouldBe("merchant_id=1&merchant_key=%2A%2A%2A&user_phone=%2A%2A%2A");
        Redaction.Body("<html>card 4111</html>", "text/html").ShouldBe("[22 characters not logged]");
        Redaction.Body("""{"conversationId":"c-1"}""", "application/json", Redaction.With("conversationId")).ShouldBe("""{"conversationId":"***"}""");
    }

    [Theory]
    [Trait("Requirement", "PAY-26")]
    [InlineData(FailureCode.InsufficientFunds, FailureCategory.Retry)]
    [InlineData(FailureCode.Abandoned, FailureCategory.Retry)]
    [InlineData(FailureCode.ExpiredCard, FailureCategory.NewPaymentMethod)]
    [InlineData(FailureCode.AuthorizationRevoked, FailureCategory.NewPaymentMethod)]
    [InlineData(FailureCode.AuthenticationRequired, FailureCategory.Authenticate)]
    [InlineData(FailureCode.MerchantNotPermitted, FailureCategory.Merchant)]
    [InlineData(FailureCode.ProviderUnavailable, FailureCategory.Merchant)]
    public void FailureCodes_HaveTheirCategory(FailureCode code, FailureCategory category)
    {
        FailureCodes.CategoryOf(code).ShouldBe(category);
    }

    [Fact]
    public void EveryFailureCode_IsCategorized_OnPurpose()
    {
        Enum.GetValues<FailureCode>().GroupBy(FailureCodes.CategoryOf).Select(group => group.Key).Order()
            .ShouldBe([FailureCategory.Retry, FailureCategory.NewPaymentMethod, FailureCategory.Authenticate, FailureCategory.Merchant]);
    }
}
