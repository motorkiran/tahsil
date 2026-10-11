using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Tests.Conformance;

/// <summary>Cards the conformance suite saves for its charges: one that pays and two that are refused.</summary>
public enum TestCardScenario
{
    Succeeds,
    InsufficientFunds,
    Declined,
}

/// <summary>
/// The adapter contract against a real sandbox (ADP-17): what runs without a browser, the same for every provider.
/// The simulator runs it in every build; real providers when their sandbox credentials are set, and are skipped otherwise.
/// </summary>
public abstract class ConformanceSuite
{
    protected abstract IPaymentProvider Provider { get; }

    /// <summary>Why the suite cannot run here, such as missing sandbox credentials; <see langword="null"/> when it can.</summary>
    protected abstract string? Unavailable { get; }

    protected virtual Amount Amount => new(1_000, "TRY", 2);

    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    protected abstract ProviderContext Context();

    /// <summary>A card saved at the provider by the test fixture, never by the adapter.</summary>
    protected abstract Task<SavedCardReference> SaveCardAsync(TestCardScenario scenario);

    [Fact]
    [Trait("Requirement", "ADP-17")]
    public async Task TheConnectionTest_Succeeds()
    {
        Assert.SkipWhen(Unavailable is not null, Unavailable ?? string.Empty);

        (await Provider.TestConnectionAsync(Context(), CancellationToken)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    [Trait("Requirement", "ADP-17")]
    public async Task ASavedCardCharge_Succeeds_IsFoundByItsReference_AndIsRefunded()
    {
        Assert.SkipWhen(Unavailable is not null, Unavailable ?? string.Empty);
        var reference = NewReference();
        var context = Context();

        var charged = await Provider.ChargeSavedCardAsync(context, Charge(reference, await SaveCardAsync(TestCardScenario.Succeeds)), CancellationToken);
        var found = await Provider.FindPaymentAsync(context, new PaymentQuery(reference, charged.ProviderPaymentId, context.Time.GetUtcNow()), CancellationToken);
        var refunded = await Provider.RefundAsync(context, new RefundRequest
        {
            RefundId = Guid.CreateVersion7(),
            Reference = NewReference(),
            PaymentReference = reference,
            ProviderPaymentId = charged.ProviderPaymentId,
            PaymentAmount = Amount,
            PaymentSucceededAt = context.Time.GetUtcNow(),
            Amount = Amount,
            PaymentProviderData = charged.ProviderData,
        }, CancellationToken);

        charged.Status.ShouldBe(PaymentStatus.Succeeded, charged.ProviderFailureMessage);
        found.ShouldBeOfType<PaymentLookup.Found>().Outcome.Status.ShouldBe(PaymentStatus.Succeeded);
        refunded.Status.ShouldBeOneOf(RefundStatus.Succeeded, RefundStatus.Pending);
    }

    [Theory]
    [Trait("Requirement", "ADP-17")]
    [InlineData(TestCardScenario.InsufficientFunds, FailureCode.InsufficientFunds)]
    [InlineData(TestCardScenario.Declined, FailureCode.CardDeclined)]
    public async Task RefusedCharges_FailWithTheirCodes(TestCardScenario scenario, FailureCode failure)
    {
        Assert.SkipWhen(Unavailable is not null, Unavailable ?? string.Empty);

        var charged = await Provider.ChargeSavedCardAsync(Context(), Charge(NewReference(), await SaveCardAsync(scenario)), CancellationToken);

        (charged.Status, charged.FailureCode).ShouldBe((PaymentStatus.Failed, failure));
    }

    [Fact]
    [Trait("Requirement", "ADP-17")]
    public async Task AnUnknownReference_IsNotFound()
    {
        Assert.SkipWhen(Unavailable is not null, Unavailable ?? string.Empty);
        var context = Context();

        (await Provider.FindPaymentAsync(context, new PaymentQuery(NewReference(), null, context.Time.GetUtcNow()), CancellationToken)).ShouldBeOfType<PaymentLookup.NotFound>();
    }

    [Fact]
    [Trait("Requirement", "ADP-17")]
    public async Task ASavedCard_IsRemoved()
    {
        Assert.SkipWhen(Unavailable is not null, Unavailable ?? string.Empty);

        (await Provider.RemoveSavedCardAsync(Context(), await SaveCardAsync(TestCardScenario.Succeeds), CancellationToken)).Removed.ShouldBeTrue();
    }

    protected static CustomerInfo Customer() => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "Ayşe Yılmaz",
        Email = "ayse@example.com",
        IpAddress = "85.34.78.112",
        City = "Istanbul",
        Country = "TR",
        AddressLine = "Moda Cad. 1, Kadıköy",
    };

    private static string NewReference() => Guid.CreateVersion7().ToString("N");

    private ChargeRequest Charge(string reference, SavedCardReference card) => new()
    {
        PaymentId = Guid.CreateVersion7(),
        Reference = reference,
        Amount = Amount,
        SavedCard = card,
        Customer = Customer(),
        Description = "Tahsil conformance",
    };
}
