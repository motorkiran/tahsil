using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Notifications;

namespace Tahsil.Server.Tests.Notifications;

/// <summary>Every customer and operator template renders in both languages, with HTML escaped in the HTML part only (NTF-8).</summary>
public sealed class EmailTemplateTests(TahsilServerFixture server)
{
    private const string Hostile = "<script>alert(1)</script>";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Templates()
    {
        var data = new TheoryData<string, string>();
        foreach (var template in Models.Keys)
        {
            data.Add(template, "tr");
            data.Add(template, "en");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Templates))]
    [Trait("Requirement", "NTF-8")]
    [Trait("Requirement", "NTF-5")]
    public async Task ATemplate_RendersInBothLanguages_EscapingHtmlOnlyInTheHtmlPart(string template, string locale)
    {
        var renderer = server.Services.GetRequiredService<IEmailTemplateRenderer>();

        var rendered = await renderer.RenderAsync(template, locale, Models[template](), CancellationToken);

        rendered.Subject.ShouldNotBeNullOrWhiteSpace();
        rendered.Subject.ShouldNotContain("{{");
        rendered.Html.ShouldContain("&lt;script&gt;");
        rendered.Html.ShouldNotContain(Hostile);
        rendered.Text.ShouldContain(Hostile);
        rendered.Text.ShouldNotContain("&lt;");
    }

    private static readonly Dictionary<string, Func<Dictionary<string, object?>>> Models = new(StringComparer.Ordinal)
    {
        ["invoice-issued"] = () => With(Invoice()),
        ["invoice-reminder"] = () => With(Invoice(), ("reminder", Map(("days_left", 3), ("due_today", false), ("days_overdue", 0)))),
        ["receipt"] = () => With(("invoice", Map(
            ("number", "INV-2026-000001"),
            ("lines", new List<object?> { Map(("description", Hostile), ("amount", "₺300,00")) }),
            ("tax", "₺50,00"),
            ("total", "₺300,00"),
            ("paid", "₺300,00"),
            ("paid_at", "1 Nisan 2026"),
            ("card", "visa •••• 4242"),
            ("legal_invoice_number", "ABC2026000000001"),
            ("hosted_url", "https://billing.example.com/invoices/x")))),
        ["payment-failed"] = () => With(Invoice(), ("failure", Map(("message", "Your card was declined."), ("new_card_needed", false), ("next_attempt", "2 May 2026")))),
        ["payment-action-required"] = () => With(Invoice()),
        ["dunning-final-reminder"] = () => With(Invoice(), ("cancel_date", "8 May 2026")),
        ["subscription-canceled"] = () => With(("subscription", Map(("plan", "Team (Monthly) × 3"), ("ended_at", "1 May 2026"), ("reason", "payment_failed")))),
        ["refund-issued"] = () => With(("refund", Map(
            ("amount", "₺100,00"), ("payment_date", "1 April 2026"), ("payment_amount", "₺300,00"), ("card", "visa •••• 4242"), ("invoice_number", "INV-2026-000001"), ("void", false)))),
        ["price-change"] = () => With(("subscription", Map(
            ("plan", "Team (Monthly) × 1"), ("new_plan", "Team Plus (Monthly) × 1"), ("old_amount", "₺300,00"), ("new_amount", "₺600,00"), ("effective_date", "1 June 2026")))),
        ["operator-attention"] = () => With(("items", new List<object?> { Item() }), ("admin_url", "https://billing.example.com/admin/attention")),
        ["operator-digest"] = () => With(("items", new List<object?> { Item(), Item() }), ("admin_url", "https://billing.example.com/admin/attention")),
    };

    private static (string, object?) Invoice() => ("invoice", Map(
        ("number", "INV-2026-000001"), ("amount", "₺300,00"), ("total", "₺300,00"), ("due_date", "15 Nisan 2026"), ("hosted_url", "https://billing.example.com/invoices/x")));

    private static Dictionary<string, object?> Item() => Map(("kind", "merchant_failures"), ("subject", Hostile), ("raised_at", "1 May 2026"), ("details", new List<object?> { Hostile }));

    /// <summary>A model with the customer and merchant every customer email has, both named hostilely.</summary>
    private static Dictionary<string, object?> With(params (string Key, object? Value)[] values)
    {
        var model = Map(values);
        model["customer"] = Map(("name", Hostile));
        model["merchant"] = Map(("name", Hostile), ("legal_name", "Örnek A.Ş."), ("address", "İstanbul"), ("phone", "+90 212 000 00 00"), ("email", "destek@example.com"));
        model["bank_transfer"] = Map(("bank", "Ziraat"), ("account_holder", "Örnek A.Ş."), ("iban", "TR00 0000"));
        return model;
    }

    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] values) => values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
}
