using Tahsil.Application.Kimlik;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Server.Tests.Kimlik;

/// <summary>Each row of KML-13: what Kimlik should show for a Tahsil subscription.</summary>
public sealed class KimlikDesiredTests
{
    private static readonly DateTimeOffset PeriodEnd = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TrialEnd = new(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly KimlikOptions Keep = new();
    private static readonly KimlikOptions EndAtOnce = new() { PastDueAccess = KimlikOptions.EndImmediately };

    [Theory]
    [Trait("Requirement", "KML-13")]
    [InlineData(SubscriptionStatus.Canceled)]
    [InlineData(SubscriptionStatus.IncompleteExpired)]
    public void AnEndedSubscription_IsEndedInKimlik(SubscriptionStatus status) =>
        KimlikStates.Desired(Facts(status), Keep).ShouldBe(KimlikDesired.Ended);

    [Fact]
    [Trait("Requirement", "KML-13")]
    public void AnIncompleteSubscription_HasNoKimlikSubscription_SinceNothingIsPaid() =>
        KimlikStates.Desired(Facts(SubscriptionStatus.Incomplete), Keep).ShouldBe(KimlikDesired.None);

    [Fact]
    [Trait("Requirement", "KML-13")]
    [Trait("Requirement", "KML-14")]
    public void ATrial_IsATrialInKimlik_UntilItsEndAndTheBuffer()
    {
        KimlikStates.Desired(Facts(SubscriptionStatus.Trialing), Keep).ShouldBe(new KimlikDesired(KimlikDesiredKind.Trialing, "pro", TrialEnd.AddDays(1)));
        KimlikStates.Desired(Facts(SubscriptionStatus.Trialing) with { CancelAt = TrialEnd }, Keep)
            .ShouldBe(new KimlikDesired(KimlikDesiredKind.Trialing, "pro", TrialEnd), "a trial canceled at its end ends there, without a buffer");
    }

    [Fact]
    [Trait("Requirement", "KML-13")]
    [Trait("Requirement", "KML-14")]
    public void AnActiveSubscription_IsActiveUntilItsPeriodEndsAndTheBuffer_OrCanceledUntilItsCancellation()
    {
        KimlikStates.Desired(Facts(SubscriptionStatus.Active), Keep).ShouldBe(new KimlikDesired(KimlikDesiredKind.Active, "pro", PeriodEnd.AddDays(1)));
        KimlikStates.Desired(Facts(SubscriptionStatus.Active), new KimlikOptions { SyncBuffer = TimeSpan.FromHours(2) }).At.ShouldBe(PeriodEnd.AddHours(2));
        KimlikStates.Desired(Facts(SubscriptionStatus.Active) with { CancelAt = PeriodEnd }, Keep).ShouldBe(new KimlikDesired(KimlikDesiredKind.CancelsAt, "pro", PeriodEnd));
    }

    [Fact]
    [Trait("Requirement", "KML-13")]
    [Trait("Requirement", "KML-15")]
    public void APastDueSubscription_KeepsItsPlanUntilTheFinalAction_UnlessConfiguredToEndAtOnce()
    {
        var finalAction = PeriodEnd.AddDays(8);
        KimlikStates.Desired(Facts(SubscriptionStatus.PastDue) with { FinalActionAt = finalAction }, Keep)
            .ShouldBe(new KimlikDesired(KimlikDesiredKind.Active, "pro", finalAction.AddDays(1)));
        KimlikStates.Desired(Facts(SubscriptionStatus.PastDue) with { FinalActionAt = PeriodEnd.AddDays(-20) }, Keep).At
            .ShouldBe(PeriodEnd.AddDays(1), "never before the paid period ends");
        KimlikStates.Desired(Facts(SubscriptionStatus.PastDue), Keep).At.ShouldBe(PeriodEnd.AddDays(1), "an invoice sent to pay has no final action");
        KimlikStates.Desired(Facts(SubscriptionStatus.PastDue) with { FinalActionAt = finalAction }, EndAtOnce).ShouldBe(KimlikDesired.Ended);
    }

    [Fact]
    [Trait("Requirement", "KML-13")]
    public void ASubscriptionNotMirroredAnyMore_IsEndedInKimlik()
    {
        KimlikStates.Desired(Facts(SubscriptionStatus.Active) with { Plan = null }, Keep).ShouldBe(KimlikDesired.Ended, "its product lost its Kimlik plan");
        KimlikStates.Desired(Facts(SubscriptionStatus.Active) with { Linked = false }, Keep).ShouldBe(KimlikDesired.Ended, "its customer was unlinked");
    }

    private static KimlikFacts Facts(SubscriptionStatus status) => new(status, TrialEnd, PeriodEnd, null, "pro", true, null);
}
