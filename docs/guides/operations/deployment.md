# Deploying Tahsil

Tahsil is one container next to PostgreSQL; it needs nothing else. This guide covers what a production
installation needs around it. The [configuration reference](../../reference/configuration.md) lists every setting, and
[going live](go-live.md) is the checklist before real payments.

## The image

Releases publish `ghcr.io/motorkiran/tahsil:<version>` for `linux/amd64` and `linux/arm64`. It runs as a non-root
user on port 8080, includes ICU and time zone data, and is configured through environment variables. To build it
yourself: `dotnet publish src/Tahsil.Server -t:PublishContainer`.

The minimum configuration:

```sh
ConnectionStrings__Tahsil="Host=db;Database=tahsil;Username=tahsil;Password=…"
Tahsil__Server__PublicUrl=https://billing.example.com/
Tahsil__Server__Mode=Live
Tahsil__Billing__TimeZone=Europe/Istanbul
Tahsil__Security__MasterKey=…     # openssl rand -base64 32; see "Keys" below
Tahsil__Merchant__LegalName=…     # and the other merchant details your legal documents print
```

Tahsil checks its configuration when it starts and refuses to run with an invalid one, naming every problem.

## Probes

- `/health/live`: the process is up. Use it for restarts.
- `/health/ready`: the database answers and has every migration. Use it to send traffic. It reports `Degraded`
  (still `200`) when a worker queue has waited more than ten minutes: requests still work, but look at the workers.

## Database and migrations

PostgreSQL 18, which Tahsil is tested with. Tahsil migrates the database when it starts (`Tahsil:Database:MigrateOnStartup`, on by
default); instances that start together take turns. To migrate as a separate deployment step instead, turn it off
and run `dotnet Tahsil.Server.dll migrate` with the same configuration. Migrations stay compatible with the previous
version, so instances can be upgraded one at a time.

## Instances

Run as many instances as you like on one database: workers share the due items through it, each item processed by
one instance, and periodic jobs run on one instance at a time. Two settings split roles:

- `Tahsil:Workers:Enabled=false` on instances that should only serve requests.
- `Tahsil:Admin:Enabled=false` on public instances, so that only an internal one serves the admin panel. The panel
  keeps a connection per browser: its instances need sticky sessions.

Data Protection keys are kept in the database, so cookies and anti-forgery tokens work across instances.

## Behind a reverse proxy

Terminate TLS at your proxy or load balancer, and tell Tahsil which proxies to believe:

```sh
Tahsil__Server__TrustedProxies__0=10.0.0.0/8
```

Only from those addresses does Tahsil take the client's address and scheme from `X-Forwarded-For` and
`X-Forwarded-Proto`. Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, which believes anyone: the client's address
feeds rate limits, the evidence of consents and the requests Tahsil sends to providers. Keep `RequireHttps` on; Live
mode requires it.

## Outbound traffic

Tahsil calls the payment providers you connect, Kimlik if you use it, your SMTP server, and your webhook endpoints.
Some providers accept calls only from addresses you register with them: give every instance the same fixed egress
address, such as through a NAT gateway, and register that. Webhooks go to public addresses only, unless
`Tahsil:Webhooks:AllowPrivateNetworks` allows an application on your internal network.

## Keys

The master key (`Tahsil:Security:MasterKey`) encrypts provider credentials, the providers' card tokens, tax IDs and
webhook secrets. Keep it in your secret store, and back it up **apart from the database**: a database without its key
is useless, and if you lose the key, every provider has to be reconnected and every customer has to give their card
again. Rotating it is a [runbook](runbooks.md#rotate-the-master-key).

## Backups

Back up PostgreSQL with point-in-time recovery: a daily full backup and continuous WAL archiving, kept as long as your
record-keeping obligations ask (ten years for invoices in Turkey). Practise restoring. After a restore, follow
[the runbook](runbooks.md#restore-from-a-backup): payments that were in flight are reconciled with the providers.

## Time

Set `Tahsil:Billing:TimeZone` before you go live: renewals, invoice numbering years and printed dates follow it, and
changing it later moves them. Keep the hosts' clocks synchronized (NTP).

## Monitoring

Tahsil exports OpenTelemetry traces, metrics and logs when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Alert on:

| Alert | Condition |
|---|---|
| Not ready | `/health/ready` failing for 2 minutes |
| Workers behind | `tahsil.worker.lag` above 600 seconds for any `queue`, or `/health/ready` reporting `Degraded` |
| Payments awaiting the provider | The dashboard's count above 10 for 30 minutes; after seven days without an answer a payment becomes an attention item |
| Attention items | Any new item: set `Tahsil:Notifications:OperatorEmail` to be emailed about each |
| Decline spike | `tahsil.checkout.decline_spikes` increasing: possible card testing on a checkout |
| Merchant failures | `tahsil.dunning.merchant_failures` increasing: a provider refuses charges for a reason on your side |
| Webhook endpoint failing | An endpoint failing for more than an hour (admin panel, **Webhooks**) |
| Kimlik sync failing | An attention item of kind Kimlik sync |
| Worker failures | `tahsil.worker.attention_raised` increasing: an item failed ten times in a row |

The other metrics: `tahsil.worker.items_processed` and `tahsil.worker.item_duration` per worker,
`tahsil.worker.item_failures`, `tahsil.payments`, `tahsil.provider_notifications`, `tahsil.dunning.recovered` and
`tahsil.dunning.canceled`, beside ASP.NET Core's, the HTTP client's, the runtime's and Npgsql's.
