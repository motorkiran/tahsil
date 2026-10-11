using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Subscriptions;

/// <summary>Changing subscriptions (SUB §4, §5) in the period [2026-04-01, 2026-05-01) of the MNY §9.1 vectors.</summary>
public sealed class SubscriptionChangeTests(TahsilServerFixture server)
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(April);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Requirement", "SUB-20")]
    [Trait("Requirement", "SUB-24")]
    [Trait("Requirement", "MNY-30")]
    [InlineData("team-monthly-try", 1, "team-plus-monthly-try", 1, 15, -15_000L, 30_000L, -2_500L, 5_000L)]
    [InlineData("starter-monthly-try", 1, "growth-monthly-try", 1, 10, -19_933L, 33_267L, null, null)]
    [InlineData("team-monthly-try", 3, "team-monthly-try", 5, 15, -45_000L, 75_000L, -7_500L, 12_500L)]
    public async Task AChangeNow_IsBilledAtOnceByTheVectors_ExactlyAsPreviewed(
        string from, int fromQuantity, string to, int toQuantity, int days, long credit, long charge, long? creditTax, long? chargeTax)
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await PayingCompanyAsync(client, from, fromQuantity);
        _time.Advance(TimeSpan.FromDays(days));
        var change = new { items = new[] { new { price = to, quantity = toQuantity } } };

        var preview = await SendAsync<SubscriptionPreviewResponse>(client, HttpMethod.Post, $"/api/v1/subscriptions/{subscription.Id}/preview", change);
        var changed = await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{subscription.Id}", change);

        var invoice = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{changed.LatestInvoice!.Id}");
        (invoice.BillingReason, invoice.Status, invoice.AmountPaid).ShouldBe((BillingReason.SubscriptionUpdate, InvoiceStatus.Paid, credit + charge));
        invoice.Lines.Select(line => (line.Type, line.Amount)).ShouldBe([(InvoiceLineType.Proration, credit), (InvoiceLineType.Proration, charge)]);
        if (creditTax is not null)
        {
            invoice.Lines.Select(line => line.TaxAmount).ShouldBe([creditTax.Value, chargeTax!.Value]);
        }

        var previewed = preview.Invoice.ShouldNotBeNull();
        previewed.Lines.Select(Comparable).ShouldBe(invoice.Lines.Select(Comparable));
        (previewed.Total, previewed.TaxTotal, previewed.AppliedBalance, previewed.AmountDue).ShouldBe((invoice.Total, invoice.TaxTotal, invoice.AppliedBalance, invoice.AmountDue));
        (invoice.PeriodStart, invoice.PeriodEnd).ShouldBe((April.AddDays(days), April.AddMonths(1)));
        changed.Items.ShouldHaveSingleItem().Quantity.ShouldBe(toQuantity);
    }

    [Fact]
    [Trait("Requirement", "SUB-23")]
    [Trait("Requirement", "INV-20")]
    public async Task ADowngradeNow_CreditsTheBalance_WhichTheNextRenewalSpends()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await PayingCompanyAsync(client, "team-plus-monthly-try");
        _time.Advance(TimeSpan.FromDays(15));

        var changed = await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{subscription.Id}", new { items = Items("team-monthly-try") });
        var credit = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{changed.LatestInvoice!.Id}");
        (credit.Total, credit.AppliedBalance, credit.AmountDue, credit.Status).ShouldBe((-15_000L, -15_000L, 0L, InvoiceStatus.Paid));
        (await BalanceAsync(host)).ShouldBe(15_000L);

        await RenewAsync(host, changed.CurrentPeriodEnd);

        var renewal = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{(await GetSubscriptionAsync(client, subscription.Id)).LatestInvoice!.Id}");
        (renewal.Total, renewal.AppliedBalance, renewal.AmountDue, renewal.Status).ShouldBe((30_000L, 15_000L, 15_000L, InvoiceStatus.Paid));
        (await BalanceAsync(host)).ShouldBe(0L);
    }

    [Fact]
    [Trait("Requirement", "SUB-20")]
    [Trait("Requirement", "SUB-24")]
    public async Task ProrationsLeftForTheRenewal_JoinIt_AsTheUpcomingInvoiceShowed()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await PayingCompanyAsync(client, "team-monthly-try", 3);
        _time.Advance(TimeSpan.FromDays(15));

        var changed = await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{subscription.Id}", new
        {
            items = Items("team-monthly-try", 5),
            prorationBehavior = "create_prorations",
        });
        var upcoming = await GetAsync<InvoicePreviewResponse>(client, $"/api/v1/invoices/upcoming?subscription={subscription.Id}");
        await RenewAsync(host, changed.CurrentPeriodEnd);

        changed.LatestInvoice!.Id.ShouldBe(subscription.LatestInvoice!.Id, "nothing was invoiced at the change");
        var renewal = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{(await GetSubscriptionAsync(client, subscription.Id)).LatestInvoice!.Id}");
        renewal.Lines.Select(line => (line.Type, line.Amount)).ShouldBe(
            [(InvoiceLineType.Subscription, 150_000L), (InvoiceLineType.Proration, -45_000L), (InvoiceLineType.Proration, 75_000L)]);
        upcoming.Lines.Select(Comparable).ShouldBe(renewal.Lines.Select(Comparable));
        (upcoming.Total, upcoming.AmountDue, upcoming.PeriodStart).ShouldBe((renewal.Total, renewal.AmountDue, renewal.PeriodStart));
    }

    [Fact]
    [Trait("Requirement", "SUB-20")]
    public async Task WithoutProration_TheNewItemsBillFromTheNextRenewal()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await PayingCompanyAsync(client, "team-monthly-try", 3);
        _time.Advance(TimeSpan.FromDays(15));

        var changed = await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{subscription.Id}", new
        {
            items = Items("team-monthly-try", 5),
            prorationBehavior = "none",
        });

        changed.LatestInvoice!.Id.ShouldBe(subscription.LatestInvoice!.Id);
        (await GetAsync<InvoicePreviewResponse>(client, $"/api/v1/invoices/upcoming?subscription={subscription.Id}")).Total.ShouldBe(150_000L);
    }

    [Fact]
    [Trait("Requirement", "SUB-19")]
    [Trait("Requirement", "SUB-12")]
    public async Task AChangeAtThePeriodEnd_WaitsForTheRenewal_AndCanBeDropped()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var downgraded = await PayingCompanyAsync(client, "team-monthly-try", 3);
        var kept = await CreateAsync(client, new CreateSubscriptionRequest { Customer = downgraded.CustomerId, Items = [new() { Price = "starter-monthly-try" }] });
        var scheduled = await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{downgraded.Id}", new
        {
            items = Items("team-monthly-try", 1),
            effective = "period_end",
        });
        await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{kept.Id}", new { items = Items("growth-monthly-try"), effective = "period_end" });
        var dropped = await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{kept.Id}", new { scheduledChange = (object?)null });

        (scheduled.ScheduledChange!.EffectiveAt, scheduled.Items.Single().Quantity).ShouldBe((April.AddMonths(1), 3));
        dropped.ScheduledChange.ShouldBeNull();
        (await GetAsync<InvoicePreviewResponse>(client, $"/api/v1/invoices/upcoming?subscription={downgraded.Id}")).Total.ShouldBe(30_000L);

        await RenewAsync(host, April.AddMonths(1));

        var renewed = await GetSubscriptionAsync(client, downgraded.Id);
        (renewed.Items.Single().Quantity, renewed.ScheduledChange).ShouldBe((1, null));
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{renewed.LatestInvoice!.Id}")).Total.ShouldBe(30_000L);
        (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{(await GetSubscriptionAsync(client, kept.Id)).LatestInvoice!.Id}")).Total.ShouldBe(29_900L);
    }

    [Fact]
    [Trait("Requirement", "SUB-21")]
    [Trait("Requirement", "MNY-28")]
    public async Task AnotherInterval_RestartsThePeriodNow_CreditingTheUnusedTime()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await PayingCompanyAsync(client, "team-monthly-try");
        _time.Advance(TimeSpan.FromDays(15));
        using var atPeriodEnd = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new { items = Items("team-yearly-try"), effective = "period_end" });
        var changed = await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{subscription.Id}", new { items = Items("team-yearly-try") });

        (await atPeriodEnd.ProblemCodeAsync()).ShouldBe("subscription.interval_change_requires_now");
        var now = April.AddDays(15);
        (changed.BillingCycleAnchor, changed.CurrentPeriodStart, changed.CurrentPeriodEnd).ShouldBe((now, now, now.AddYears(1)));
        var invoice = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{changed.LatestInvoice!.Id}");
        invoice.Lines.Select(line => (line.Type, line.Amount)).ShouldBe([(InvoiceLineType.Proration, -15_000L), (InvoiceLineType.Subscription, 300_000L)]);
        (invoice.PeriodStart, invoice.PeriodEnd, invoice.Status).ShouldBe((now, now.AddYears(1), InvoiceStatus.Paid));
    }

    [Fact]
    [Trait("Requirement", "SUB-22")]
    [Trait("Requirement", "SUB-17")]
    public async Task DuringATrial_ChangesApplyWithoutProration_AndTheTrialCanEndNow()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CompanyAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var trial = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "pro-monthly-try" }] });

        var changed = await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{trial.Id}", new { items = Items("pro-monthly-try", 2) });
        _time.Advance(TimeSpan.FromDays(3));
        var ended = await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{trial.Id}", new { trialEnd = "now" });

        (changed.Status, changed.TrialEnd, changed.LatestInvoice).ShouldBe((SubscriptionStatus.Trialing, trial.TrialEnd, null));
        var now = April.AddDays(3);
        (ended.TrialEnd, ended.CurrentPeriodStart, ended.CurrentPeriodEnd).ShouldBe((now, now, now.AddMonths(1)));
        var first = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{ended.LatestInvoice!.Id}");
        (first.BillingReason, first.Total, first.PeriodStart).ShouldBe((BillingReason.SubscriptionCycle, 100_000L, now));
    }

    [Theory]
    [Trait("Requirement", "SUB-34")]
    [InlineData("individual", "now", null, false, "subscription.price_increase_notice_required")]
    [InlineData("individual", "period_end", null, false, "subscription.price_increase_notice_required")]
    [InlineData("individual", "now", null, true, "subscription.price_increase_notice_required")]
    [InlineData("individual", "period_end", 61, false, null)]
    [InlineData("company", "now", null, false, null)]
    [InlineData("company", "now", null, true, null)]
    public async Task PriceIncreasesForIndividuals_NeedNotice(string type, string effective, int? effectiveInDays, bool skipNotice, string? refused)
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = type == "company" ? await CompanyAsync(client) : await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        _time.Advance(TimeSpan.FromDays(1));

        using var response = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new
        {
            items = Items("team-plus-monthly-try"),
            effective,
            effectiveAt = effectiveInDays is { } days ? April.AddDays(days) : (DateTimeOffset?)null,
            skipNotice,
        });

        if (refused is null)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        }
        else
        {
            (await response.ProblemCodeAsync()).ShouldBe(refused);
        }
    }

    [Fact]
    [Trait("Requirement", "SUB-34")]
    public async Task ALowerPriceForAnIndividual_AppliesAtOnce()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-plus-monthly-try" }] });

        using var response = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new { items = Items("team-monthly-try") });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    [Trait("Requirement", "SUB-26")]
    [Trait("Requirement", "SUB-27")]
    [Trait("Requirement", "API-5")]
    public async Task TheCardAndTheCollectionMethod_Change_GuardedByIfMatch()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CompanyAsync(client);
        var stranger = await SaveCardAsync(client, await CustomerAsync(client), "4242424242424242");
        var card = await SaveCardAsync(client, customer, "4000000000007726");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        using var read = await client.GetPathAsync($"/api/v1/subscriptions/{subscription.Id}");
        var etag = read.Headers.ETag!;

        using var strangers = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new { defaultPaymentMethod = stranger });
        var changed = await SendAsync<SubscriptionResponse>(client, HttpMethod.Patch, $"/api/v1/subscriptions/{subscription.Id}", new
        {
            defaultPaymentMethod = card,
            collectionMethod = "send_invoice",
            daysUntilDue = 10,
        }, etag.Tag);
        using var stale = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new { metadata = new Dictionary<string, string> { ["a"] = "b" } }, etag.Tag);

        (await strangers.ProblemCodeAsync()).ShouldBe("subscription.payment_method_unusable");
        (changed.DefaultPaymentMethodId, changed.CollectionMethod, changed.DaysUntilDue).ShouldBe((card, CollectionMethod.SendInvoice, 10));
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    private static object[] Items(string price, int quantity = 1) => [new { price, quantity }];

    private static (InvoiceLineType, string, long, long, decimal) Comparable(InvoiceLineResponse line) => (line.Type, line.Description, line.Amount, line.TaxAmount, line.Quantity);

    /// <summary>A company's subscription paid with a saved card at the start of April.</summary>
    private static async Task<SubscriptionResponse> PayingCompanyAsync(HttpClient client, string price, int quantity = 1)
    {
        var customer = await CompanyAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = price, Quantity = quantity }] });
        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        return subscription;
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string path, object body, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body, options: ApiClients.Json) };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(ifMatch));
        }

        using var response = await client.SendAsync(request, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    /// <summary>Moves to the renewal, lets the cycle invoice finalize after its draft period, and collects it.</summary>
    private async Task RenewAsync(WebApplicationFactory<Program> host, DateTimeOffset periodEnd)
    {
        _time.Advance(periodEnd - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
        _time.Advance(TimeSpan.FromHours(1));
        await host.RunWorkersUntilIdleAsync();
    }

    private static async Task<long> BalanceAsync(WebApplicationFactory<Program> host) =>
        (await host.QueryAsync<long>($"SELECT coalesce(sum(amount), 0)::bigint AS \"Value\" FROM tahsil.customer_balances")).Single();

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("changes"), _time);
}
