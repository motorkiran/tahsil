using Tahsil.Domain.Invoices;

namespace Tahsil.Domain.Tests.Invoices;

/// <summary>When failed automatic payments are tried again (DUN-1, DUN-7).</summary>
public sealed class DunningScheduleTests
{
    private static readonly DateTimeOffset First = new(2026, 5, 1, 1, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan[] Schedule = [TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(5), TimeSpan.FromDays(7)];

    [Theory]
    [Trait("Requirement", "DUN-1")]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(2.5, 3)]
    [InlineData(5, 7)]
    [InlineData(7, null)]
    public void CustomerRetries_FollowTheOffsetsFromTheFirstFailure(double daysLater, int? retryDay) =>
        DunningSchedule.NextRetry(First, Schedule, First.AddDays(daysLater)).ShouldBe(retryDay is { } day ? First.AddDays(day) : null);

    [Theory]
    [Trait("Requirement", "DUN-7")]
    [InlineData(0, 0.25)]
    [InlineData(0.25, 1.25)]
    [InlineData(1.25, 7.25)]
    [InlineData(7.25, 31.25)]
    [InlineData(31.25, 55.25)]
    [InlineData(60, 79.25)]
    public void MerchantRetries_BackOffTechnically_ThenDaily(double hoursLater, double nextHours) =>
        DunningSchedule.NextTechnicalRetry(First, First.AddHours(hoursLater)).ShouldBe(First.AddHours(nextHours));

    [Fact]
    [Trait("Requirement", "DUN-1")]
    [Trait("Requirement", "DUN-3")]
    public void TheFirstCustomerFailure_SetsTheDeadline_AndCardsThatCannotWorkWait()
    {
        var invoice = Invoice.CreateDraft(Guid.CreateVersion7(), "TRY", BillingReason.Manual, CollectionMethod.ChargeAutomatically, null, null, false, Common.Metadata.Empty, First).Value;

        invoice.RecordMerchantFailure(First);
        invoice.RecordCustomerFailure(Schedule, retries: false, First.AddHours(1)).ShouldBeTrue();
        invoice.RecordCustomerFailure(Schedule, retries: true, First.AddDays(2)).ShouldBeFalse();

        (invoice.DunningStartedAt, invoice.DunningDeadlineAt, invoice.MerchantFailingSince).ShouldBe((First.AddHours(1), First.AddHours(1).AddDays(7), null));
        invoice.NextPaymentAttemptAt.ShouldBe(First.AddHours(1).AddDays(3));
    }
}
