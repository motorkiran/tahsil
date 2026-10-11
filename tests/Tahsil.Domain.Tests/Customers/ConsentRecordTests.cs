using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tahsil.Domain.Customers;

namespace Tahsil.Domain.Tests.Customers;

[Trait("Requirement", "CHK-16")]
[Trait("Requirement", "DOM-20")]
public sealed class ConsentRecordTests
{
    [Fact]
    public void ADocumentConsent_KeepsTheExactRenderedTextAndCheckbox_Compressed()
    {
        var html = "<h1>Ön Bilgilendirme Formu</h1>" + string.Concat(Enumerable.Repeat("<p>Cayma hakkı 14 gündür.</p>", 200));
        var session = Guid.CreateVersion7();

        var record = ConsentRecord.ForDocument(
            Guid.CreateVersion7(),
            ConsentType.PreInformation,
            new DocumentShown("pre-information", "2026-10", html),
            "Ön Bilgilendirme Formu'nu okudum.",
            new Dictionary<string, string> { ["amountDue"] = "59800" },
            session,
            IPAddress.Parse("203.0.113.7"),
            "Mozilla/5.0",
            DateTimeOffset.UnixEpoch);

        record.ReadDocument().ShouldBe(html);
        record.DocumentHash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
        record.RenderedDocument!.Length.ShouldBeLessThan(Encoding.UTF8.GetByteCount(html) / 10);
        (record.DocumentKey, record.DocumentVersion, record.TextShown, record.CheckoutSessionId).ShouldBe(("pre-information", "2026-10", "Ön Bilgilendirme Formu'nu okudum.", (Guid?)session));
    }
}
