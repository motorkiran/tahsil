using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Checkout;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using DomainCustomers = Tahsil.Domain.Customers;

namespace Tahsil.Server.Tests.Checkout;

/// <summary>Legal documents of the checkout (CHK §4, §5): starter templates, session data, versions and the merchant.</summary>
public sealed class LegalDocumentTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions = [TahsilPermissions.CheckoutWrite, TahsilPermissions.CatalogWrite];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Requirement", "CHK-27")]
    [Trait("Requirement", "CHK-28")]
    [InlineData("tr", "Ön Bilgilendirme Formu", "₺598,00", "Cayma hakkı")]
    [InlineData("en", "Pre-information Form", "₺598.00", "Right of withdrawal")]
    public async Task TheStarterDocuments_RenderWithTheSessionsData(string locale, string title, string total, string withdrawal)
    {
        await using var host = Host();
        var session = await PaymentSessionAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        var renderer = scope.ServiceProvider.GetRequiredService<ILegalDocumentRenderer>();
        var model = scope.ServiceProvider.GetRequiredService<LegalDocumentModels>().For(
            session, locale, new LegalDocumentCustomer("<b>Ayşe</b> Yılmaz", "ayse@example.com", DomainCustomers.CustomerType.Individual, null, null, null, null));

        var documents = new List<string>();
        foreach (var document in LegalDocumentOptions.Starters)
        {
            documents.Add(await renderer.RenderAsync(document, locale, model, CancellationToken));
        }

        documents[0].ShouldContain($"<h1>{title}</h1>");
        documents.ShouldAllBe(html => html.Contains("Acme Yazılım A.Ş.") && html.Contains(total) && html.Contains("1,000 credits") && html.Contains(session.Id.ToString()));
        documents.ShouldAllBe(html => html.Contains("&lt;b&gt;Ayşe&lt;/b&gt; Yılmaz"), "data is HTML-encoded");
        documents[0].ShouldContain(withdrawal);
        documents.ShouldAllBe(html => !html.Contains("TASLAK") && !html.Contains("DRAFT"), "the review note is a template comment");
    }

    [Fact]
    [Trait("Requirement", "CHK-11")]
    [Trait("Requirement", "CHK-26")]
    public async Task ASession_KeepsTheDocumentsOfItsMode()
    {
        await using var host = Host();
        var payment = await PaymentSessionAsync(host);
        using var client = await host.CreateClientWithAsync(Permissions);
        using var created = await client.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest { Mode = CheckoutMode.Setup, Currency = "TRY", SuccessUrl = "https://shop.example.com/done" });
        var setup = await created.ReadAsync<CheckoutSessionResponse>();

        var documents = await QueryAsync(host, context => context.CheckoutSessions.Where(session => session.Id == payment.Id || session.Id == setup.Id)
            .ToDictionaryAsync(session => session.Id, session => session.Documents, CancellationToken));

        documents[payment.Id].Select(document => (document.Key, document.Version, document.RequiresAcceptance)).ShouldBe([("pre-information", "2026-10", true), ("distance-sales", "2026-10", true)]);
        documents[payment.Id].ShouldAllBe(document => document.CustomerTypes.SequenceEqual(new[] { DomainCustomers.CustomerType.Individual }));
        documents[setup.Id].ShouldBeEmpty();
    }

    [Fact]
    [Trait("Requirement", "CHK-29")]
    public async Task AChangedTemplate_NeedsANewVersion()
    {
        var database = server.NewDatabase("legal");
        var directory = Directory.CreateTempSubdirectory("tahsil-legal-");
        var template = Path.Combine(directory.FullName, "terms.liquid");
        await File.WriteAllTextAsync(template, "<h1>{{ title }}</h1><p>Version one.</p>", CancellationToken);

        await using (var first = Host(database, Terms(template, "1")))
        {
            first.CreateClient().Dispose();
        }

        await File.WriteAllTextAsync(template, "<h1>{{ title }}</h1><p>Version two.</p>", CancellationToken);
        await using var changed = Host(database, Terms(template, "1"));
        var failure = Should.Throw<Exception>(() => changed.CreateClient());
        await using var renewed = Host(database, Terms(template, "2"));
        using var client = renewed.CreateClient();
        directory.Delete(recursive: true);

        failure.ToString().ShouldContain("The legal document 'terms' (tr) changed under version '1'");
        (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await QueryAsync(renewed, context => context.LegalDocumentVersions.Where(version => version.Key == "terms").Select(version => version.Version).ToListAsync(CancellationToken)))
            .ShouldBe(["1", "1", "2", "2"], ignoreOrder: true);
    }

    [Theory]
    [Trait("Requirement", "CHK-30")]
    [Trait("Requirement", "CHK-26")]
    [InlineData("Tahsil:Merchant:LegalName", "", "set Tahsil:Merchant:LegalName")]
    [InlineData("Tahsil:Checkout:LegalDocuments:0:Type", "terms_of_service", "Tahsil:Checkout:LegalDocuments:0:Type must be one of")]
    public async Task InvalidMerchantOrDocumentSettings_StopStartup(string key, string value, string message)
    {
        var settings = key.StartsWith("Tahsil:Checkout", StringComparison.Ordinal)
            ? new Dictionary<string, string?>
            {
                ["Tahsil:Checkout:LegalDocuments:0:Key"] = "terms",
                ["Tahsil:Checkout:LegalDocuments:0:Version"] = "1",
                ["Tahsil:Checkout:LegalDocuments:0:Title:tr"] = "Koşullar",
                ["Tahsil:Checkout:LegalDocuments:0:Url:tr"] = "https://example.com/terms",
            }
            : [];
        settings[key] = value;
        await using var host = Host(server.NewDatabase("legal"), settings);

        Should.Throw<Exception>(() => host.CreateClient()).ToString().ShouldContain(message);
    }

    private static Dictionary<string, string?> Terms(string template, string version) => new()
    {
        ["Tahsil:Checkout:LegalDocuments:0:Key"] = "terms",
        ["Tahsil:Checkout:LegalDocuments:0:Type"] = "custom",
        ["Tahsil:Checkout:LegalDocuments:0:Version"] = version,
        ["Tahsil:Checkout:LegalDocuments:0:Title:tr"] = "Kullanım Koşulları",
        ["Tahsil:Checkout:LegalDocuments:0:Template:tr"] = template,
        ["Tahsil:Checkout:LegalDocuments:0:RequiresAcceptance"] = "true",
    };

    private static async Task<Domain.Checkout.CheckoutSession> PaymentSessionAsync(WebApplicationFactory<Program> host)
    {
        using var client = await host.CreateClientWithAsync(Permissions);
        (await client.PostJsonAsync("/api/v1/products", new CreateProductRequest { Key = "credits", Name = "1,000 credits" })).EnsureSuccessStatusCode().Dispose();
        (await client.PostJsonAsync("/api/v1/prices", new CreatePriceRequest { Product = "credits", Key = "credits-1000", Currency = "TRY", Type = PriceType.OneTime, UnitAmount = 29_900 }))
            .EnsureSuccessStatusCode().Dispose();
        using var response = await client.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Payment,
            Items = [new CheckoutItemRequest { Price = "credits-1000", Quantity = 2 }],
            SuccessUrl = "https://shop.example.com/done",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var id = (await response.ReadAsync<CheckoutSessionResponse>()).Id;
        return await QueryAsync(host, context => context.CheckoutSessions.AsNoTracking().SingleAsync(session => session.Id == id, CancellationToken));
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private WebApplicationFactory<Program> Host(string? database = null, Dictionary<string, string?>? settings = null)
    {
        settings ??= [];
        settings["ConnectionStrings:Tahsil"] = database ?? server.NewDatabase("legal");
        return server.WithSettings(settings);
    }
}
