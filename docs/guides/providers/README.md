# Payment providers

Tahsil charges cards through your own account at a payment provider. Each connection holds that account's
credentials, encrypted, and the currencies routed to it decide which connection takes a customer's payment.

| Provider | Guide | Cards saved for renewals | Notes |
|---|---|---|---|
| iyzico | [iyzico.md](iyzico.md) | Yes | iyzico's hosted payment page with 3D Secure |
| Stripe | [stripe.md](stripe.md) | Yes | Stripe's hosted Checkout; TRY, USD, EUR and more |
| PayTR | [paytr.md](paytr.md) | In direct form mode | iFrame mode by default; direct form mode widens your PCI scope |
| Craftgate | [craftgate.md](craftgate.md) | Yes | Runs on your bank POSes, such as Kuveyt Türk's |
| Simulator | [Getting started](../getting-started.md#test-cards) | Yes | Test mode only: test cards that succeed, fail or time out |

## Before you take real payments

1. Create a connection in the admin panel (**Provider connections**) or a provisioning document, and test it.
2. Route your currencies to it, and register its notification URL with the provider.
3. Pay a small amount through it with a real card: **Run a test payment** on the connections page takes it through
   the same pages your customers use and refunds it at once.
4. Switch the installation to Live mode (`Tahsil:Server:Mode`): Live mode takes production connections only, and
   refuses sandbox ones.

Tahsil never sees card numbers: customers enter them on the provider's page or form, and Tahsil keeps the provider's
token. PayTR's direct form mode is the one exception to the page being the provider's; its guide explains what it
means for your PCI DSS scope.
