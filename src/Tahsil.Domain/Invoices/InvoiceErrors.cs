using Tahsil.Domain.Common;

namespace Tahsil.Domain.Invoices;

public static class InvoiceErrors
{
    public static readonly Error NotFound = Error.NotFound("invoice.not_found", "The invoice does not exist.");
    public static readonly Error NotDraft = Error.Conflict("invoice.not_draft", "Only a draft's lines and terms can change; the invoice is finalized.");
    public static readonly Error Empty = Error.Validation("invoice.empty", "The invoice has no lines to finalize.");
    public static readonly Error ZeroTotal = Error.Validation("invoice.empty", "A manual invoice's lines must add up to something other than zero.");
    public static readonly Error HasPayments = Error.Conflict("invoice.has_payments", "The invoice has payments; refund them first.");
    public static readonly Error LineNotFound = Error.NotFound("invoice.line_not_found", "The invoice has no such line.");
    public static readonly Error InvalidMemo = Error.Validation("invoice.invalid", "The memo is at most 1000 characters.", "memo");
    public static readonly Error InvalidDaysUntilDue = Error.Validation("invoice.invalid", "Days until due are between 0 and 365.", "daysUntilDue");
    public static readonly Error InvalidLegalInvoice = Error.Validation(
        "invoice.invalid", "The legal invoice has a number of at most 64 characters, and an absolute HTTPS URL of at most 2000 characters if any.", "legalInvoice");

    public static Error InvalidState(InvoiceStatus status, string action) =>
        Error.Conflict("invoice.invalid_state", $"An invoice that is {status.ToString().ToLowerInvariant()} cannot be {action}.");

    public static Error InvalidLine(string field, string message) => Error.Validation("invoice.invalid_line", message, field);
}

public static class BalanceErrors
{
    public static readonly Error InvalidDescription = Error.Validation(
        "balance.invalid", "A description of at most 500 characters says why the balance changes.", "description");

    public static readonly Error InvalidAmount = Error.Validation("balance.invalid", "The amount is a non-zero number of minor units.", "amount");
}
