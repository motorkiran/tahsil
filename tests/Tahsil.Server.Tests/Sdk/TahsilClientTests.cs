using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Tahsil.Client;
using Tahsil.Contracts;

namespace Tahsil.Server.Tests.Sdk;

/// <summary><c>Tahsil.Client</c> against the real server (SDK-4 to SDK-9).</summary>
public sealed class TahsilClientTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CustomersRead, TahsilPermissions.CustomersWrite, TahsilPermissions.CatalogRead, TahsilPermissions.CatalogWrite,
        TahsilPermissions.SubscriptionsRead, TahsilPermissions.SubscriptionsWrite, TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite,
        TahsilPermissions.UsageRead, TahsilPermissions.UsageWrite, TahsilPermissions.ProvisioningWrite, TahsilPermissions.AuditRead,
        TahsilPermissions.CheckoutWrite, TahsilPermissions.TestWrite,
    ];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "SDK-4")]
    [Trait("Requirement", "SDK-8")]
    public async Task TheClient_CallsTheApi_AndFollowsPages()
    {
        using var recorder = new Recorder();
        var tahsil = await ClientAsync(recorder);
        var key = $"pro-{Guid.CreateVersion7():N}"[..24];

        var result = await tahsil.Provisioning.ApplyAsync(new ProvisioningDocument
        {
            Products = [new ProvisionedProduct { Key = key, Name = "Pro", Prices = [new ProvisionedPrice { Key = $"{key}-monthly", Currency = "TRY", Type = PriceType.Recurring, Interval = PriceInterval.Month, IntervalCount = 1, UsageType = UsageType.Licensed, UnitAmount = 30_000 }] }],
        }, cancellationToken: CancellationToken);
        var externalIds = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var created = await tahsil.Customers.CreateAsync(new CreateCustomerRequest { Name = $"Ada {index}", Email = $"{Guid.CreateVersion7():N}@example.com", ExternalId = $"{key}-{index}" }, cancellationToken: CancellationToken);
            externalIds.Add(created.ExternalId!);
        }

        var customer = await tahsil.Customers.GetByExternalIdAsync(externalIds[0], cancellationToken: CancellationToken);
        var subscription = await tahsil.Subscriptions.CreateAsync(new CreateSubscriptionRequest
        {
            Customer = customer.Id,
            Items = [new SubscriptionItemRequest { Price = $"{key}-monthly" }],
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 14,
        }, cancellationToken: CancellationToken);
        var upcoming = await tahsil.Invoices.GetUpcomingAsync(subscription.Id, cancellationToken: CancellationToken);
        var page = await tahsil.Customers.ListAsync(search: key, limit: 2, cancellationToken: CancellationToken);
        var all = new List<CustomerResponse>();
        await foreach (var found in tahsil.Customers.ListAllAsync(search: key, limit: 1, cancellationToken: CancellationToken))
        {
            all.Add(found);
        }

        result.Items.ShouldNotBeEmpty();
        (subscription.Status, upcoming.Total).ShouldBe((SubscriptionStatus.Active, 30_000L));
        (page.Items.Count, page.NextCursor).ShouldBe((2, page.NextCursor));
        page.NextCursor.ShouldNotBeNull();
        all.Select(found => found.ExternalId).ShouldBe(externalIds, ignoreOrder: true);
        recorder.Requests.Count(request => request.Path.StartsWith("/api/v1/customers?q=", StringComparison.Ordinal)).ShouldBe(4, "one page, then three pages of one");
        recorder.Requests.Where(request => request.Method == "POST").ShouldAllBe(request => request.IdempotencyKey != null);
        recorder.Requests.Where(request => request.Method == "GET").ShouldAllBe(request => request.IdempotencyKey == null);
    }

    [Fact]
    [Trait("Requirement", "SDK-2")]
    [Trait("Requirement", "SDK-9")]
    public async Task Changes_SendWhatIsSet_AndCanExpectAVersion()
    {
        using var recorder = new Recorder();
        var tahsil = await ClientAsync(recorder);
        var created = await tahsil.Customers.CreateAsync(new CreateCustomerRequest { Name = "Ada", Email = $"{Guid.CreateVersion7():N}@example.com", Phone = "+905551112233" }, cancellationToken: CancellationToken);
        var read = new RequestOptions();
        await tahsil.Customers.GetAsync(created.Id, read, CancellationToken);

        var renamed = await tahsil.Customers.UpdateAsync(created.Id, new UpdateCustomerRequest { Name = "Ada Lovelace" }, new RequestOptions { IfMatch = read.ETag }, CancellationToken);
        var stale = await Should.ThrowAsync<TahsilApiException>(() => tahsil.Customers.UpdateAsync(created.Id, new UpdateCustomerRequest { Name = "Grace" }, new RequestOptions { IfMatch = read.ETag }, CancellationToken));
        var cleared = await tahsil.Customers.UpdateAsync(created.Id, new UpdateCustomerRequest { Phone = null }, cancellationToken: CancellationToken);

        read.ETag.ShouldNotBeNullOrEmpty();
        (renamed.Name, renamed.Phone).ShouldBe(("Ada Lovelace", "+905551112233"), "what a change does not set keeps its value");
        (stale.StatusCode, stale.Code).ShouldBe((HttpStatusCode.PreconditionFailed, "concurrency.precondition_failed"));
        (cleared.Name, cleared.Phone).ShouldBe(("Ada Lovelace", null), "null clears what it is set to");
        var patches = recorder.Requests.Where(request => request.Method == "PATCH").Select(request => request.Body).ToList();
        patches.ShouldBe(["{\"name\":\"Ada Lovelace\"}", "{\"name\":\"Grace\"}", "{\"phone\":null}"]);
    }

    [Fact]
    [Trait("Requirement", "SDK-7")]
    public async Task Refusals_AreExceptions_WithTheProblemsCodeFieldsAndTraceId()
    {
        using var recorder = new Recorder();
        var tahsil = await ClientAsync(recorder);

        var missing = await Should.ThrowAsync<TahsilApiException>(() => tahsil.Customers.GetAsync(Guid.CreateVersion7(), cancellationToken: CancellationToken));
        var invalid = await Should.ThrowAsync<TahsilApiException>(() => tahsil.Customers.CreateAsync(new CreateCustomerRequest { Email = "not an email" }, cancellationToken: CancellationToken));
        var forbidden = await Should.ThrowAsync<TahsilApiException>(() => tahsil.ApiKeys.ListAsync(cancellationToken: CancellationToken));

        (missing.StatusCode, missing.Code, missing.IsCode("customer.not_found")).ShouldBe((HttpStatusCode.NotFound, "customer.not_found", true));
        missing.TraceId.ShouldNotBeNullOrEmpty();
        missing.Message.ShouldBe("The customer does not exist.");
        (invalid.StatusCode, invalid.Code).ShouldBe((HttpStatusCode.BadRequest, "customer.invalid"));
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Requirement", "SDK-5")]
    [Trait("Requirement", "SDK-6")]
    public async Task Retries_KeepTheIdempotencyKey_AndStopAtTheLimit()
    {
        using var recorder = new Recorder { FailFirst = 1 };
        var tahsil = await ClientAsync(recorder);

        var created = await tahsil.Customers.CreateAsync(new CreateCustomerRequest { Name = "Ada", Email = $"{Guid.CreateVersion7():N}@example.com" }, new RequestOptions { IdempotencyKey = "my-key-1" }, CancellationToken);
        recorder.FailFirst = 10;
        var exhausted = await Should.ThrowAsync<TahsilApiException>(() => tahsil.Customers.GetAsync(created.Id, cancellationToken: CancellationToken));
        recorder.FailFirst = 0;
        var reused = await Should.ThrowAsync<TahsilApiException>(() => tahsil.Customers.CreateAsync(
            new CreateCustomerRequest { Name = "Ada", Email = "other@example.com" }, new RequestOptions { IdempotencyKey = "my-key-1" }, CancellationToken));

        recorder.Attempts.Take(2).Select(request => (request.Method, request.IdempotencyKey)).ShouldBe([("POST", "my-key-1"), ("POST", "my-key-1")]);
        exhausted.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        recorder.Attempts.Count(request => request.Method == "GET").ShouldBe(3, "the first try and two retries");
        reused.Code.ShouldBe("idempotency.key_reused", "a key is for one request, whatever the client does");
    }

    [Fact]
    [Trait("Requirement", "SDK-4")]
    public void EveryOperationOfTheApi_HasAMethod()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            typeof(TahsilClientTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(metadata => metadata.Key == "ProjectDirectory").Value!, "Api", "openapi-v1.json")));
        var operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Where(method => method.Name is "get" or "post" or "put" or "patch" or "delete"))
            .Count();
        var methods = typeof(ITahsilClient).GetProperties()
            .SelectMany(resource => resource.PropertyType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Count(method => method.Name.EndsWith("Async", StringComparison.Ordinal) && !method.Name.StartsWith("ListAll", StringComparison.Ordinal));

        methods.ShouldBe(operations);
    }

    private async Task<ITahsilClient> ClientAsync(Recorder recorder)
    {
        var key = await server.CreateApiKeyAsync(Permissions);
        recorder.InnerHandler = server.Server.CreateHandler();
        var http = new HttpClient(recorder) { BaseAddress = server.Server.BaseAddress };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Key);
        return new TahsilClient(http, Options.Create(new TahsilClientOptions { BaseUrl = server.Server.BaseAddress, ApiKey = key.Key }));
    }

    /// <summary>Records what the client sends, and answers <c>503</c> itself to the first calls when asked.</summary>
    private sealed class Recorder : DelegatingHandler
    {
        public int FailFirst { get; set; }

        public List<Sent> Requests { get; } = [];

        public List<Sent> Attempts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var sent = new Sent(request.Method.Method, request.RequestUri!.PathAndQuery, request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null, body);
            Attempts.Add(sent);
            if (FailFirst > 0)
            {
                FailFirst--;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
            }

            Requests.Add(sent);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record Sent(string Method, string Path, string? IdempotencyKey, string? Body);
}
