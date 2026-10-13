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

## Running locally

```sh
docker compose up -d                        # PostgreSQL on 5434 and Mailpit on 8026
dotnet run --project src/Tahsil.Server      # http://localhost:5090
```

The server migrates the database when it starts (`Tahsil:Database:MigrateOnStartup`); `dotnet Tahsil.Server.dll migrate` does it as a separate step. To run the container image instead:

```sh
dotnet publish src/Tahsil.Server -t:PublishContainer
docker compose --profile app up -d          # http://localhost:8090
```

`/health/live` and `/health/ready` are the probes for orchestrators.

With Kimlik, `docker compose --profile kimlik up -d` also runs Kimlik for the connector and the admin sign-in, and [`samples/`](samples) has an app that sells a Kimlik plan through Tahsil; [samples/README.md](samples/README.md) walks through both.

## License

Tahsil is licensed under the [Apache License 2.0](LICENSE).
