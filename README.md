# Tahsil

Tahsil ("collection", as in collecting a payment, in Turkish) is an open-source, self-hosted billing and subscription server. It sits between an application and its payment provider and runs the billing: hosted checkout with 3D Secure, saved cards, subscriptions with trials and proration, automatic renewals, payment retries and dunning emails, invoices, coupons, usage-based billing, refunds, a customer portal, signed webhooks, an admin panel and a .NET SDK.

It works with iyzico, Stripe, PayTR and Craftgate (and through Craftgate with bank POSes such as Kuveyt Türk), using the merchant's own credentials, and integrates with [Kimlik](https://github.com/motorkiran/kimlik) to turn subscriptions into plans and entitlements.

> **Status:** early development. Not usable yet.

## Platform

C# 14, .NET 10, ASP.NET Core 10, EF Core 10 and PostgreSQL; one container next to PostgreSQL, no other required services.

## Building

Tahsil needs the .NET 10 SDK; the integration tests also need Docker or Podman.

```sh
dotnet build
dotnet test
```

## License

Tahsil is licensed under the [Apache License 2.0](LICENSE).
