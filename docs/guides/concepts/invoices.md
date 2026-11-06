# Invoices

Every charge is an invoice: subscriptions' periods and changes, checkouts, and the invoices you create yourself.

## Statuses

`draft` → `open` → `paid`, or `void` (cancelled; it is kept, and its number is not reused) or `uncollectible`
(written off; a later payment still settles it). A draft can be changed: lines added or removed, its terms set.
Finalizing gives it a number (`INV-2026-000123`, gapless per year), freezes its lines, takes a snapshot of the
customer's billing details and applies the customer's balance.

## Lines and tax

Lines come from prices (with their tax rates) or are free-form (a description, an amount and an optional tax rate).
Tax is computed per line and rounded once per line; totals are sums of rounded lines, so the invoice adds up to the
kuruş. Pending **invoice items** (`POST /api/v1/invoice-items`) wait for the customer's next invoice: the next
renewal of their subscription, or the next manual invoice that includes them.

## Collection

- `charge_automatically`: Tahsil charges the customer's card when the invoice is finalized, and [dunning](dunning.md)
  retries a failure.
- `send_invoice`: Tahsil emails the invoice with a link to its **hosted invoice page**, where the customer pays by card
  or sees the bank account to transfer to (`Tahsil:Invoices:BankTransfer`). It is due `daysUntilDue` days after
  finalization; reminders go before and after (`Tahsil:Invoices:Reminders`). A bank transfer you receive is recorded
  with `POST /api/v1/invoices/{id}/pay` and `outOfBand`, or in the admin panel.

Every invoice that is not a draft has a hosted page: a receipt once paid.

## The legal invoice

Tahsil's invoices are billing documents, not e-Fatura or e-Arşiv invoices. Issue the legal invoice with your
integrator from the `invoice.finalized` or `invoice.paid` webhook, and record its number and link on the invoice
(`PATCH /api/v1/invoices/{id}` with `legalInvoice`), so the hosted page and the customer's emails show it.

## Credit and refunds

A customer's **balance** per currency holds credit, from downgrades or adjustments, which finalization applies
before any charge. **Refunds** give money back from a payment, wholly or in part (`POST /api/v1/refunds`); they never
change the invoice's status.
