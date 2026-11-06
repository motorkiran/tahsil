# Performance

Tahsil's performance targets and what one instance measured against them before v0.1.0. The numbers come from a
developer machine, not from production hardware: read them as orders of magnitude, and measure your own deployment
before relying on them.

## Results

| Area | Target | Measured |
|---|---|---|
| Management API reads (a customer, a page of 20 customers) | p95 < 100 ms | p95 3.7 ms, median 2.0 ms, 3,600 requests/s |
| Management API writes without provider calls (create a customer) | p95 < 150 ms | p95 7.8 ms, median 3.0 ms, 2,100 requests/s |
| Hosted page rendering, server time | p95 < 150 ms | checkout p95 4.2 ms, hosted invoice p95 5.0 ms, portal overview p95 15.9 ms |
| Renewal throughput with the simulator | ≥ 20,000 per hour per instance | 86,600 per hour (5,000 renewals in 208 s) |
| Usage event ingestion, in batches | ≥ 1,000 events/s per instance | 105,000 events/s (60 batches of 1,000) |
| Container image | < 200 MB | about 217 MB unpacked (171 MB of it the base image), about 85 MB to download |
| Idle memory | < 300 MB | about 230 MB on two cores |

A renewal here is the whole of it: the cycle invoice drafted at the period's end, finalized an hour later, charged to
the saved card, and its events, webhooks and emails queued.

## How it was measured

- Machine: Apple M3 Max (14 cores, 36 GB), PostgreSQL 18 in a container on the same machine, .NET 10, Release build,
  logging at `Warning`.
- The load tests are in `tests/Tahsil.Server.Tests/Performance/LoadTests.cs` and the ingestion test in
  `UsageRetentionTests`. They run against the application in process, so request times are the server's own, without a
  network. They are skipped unless `TAHSIL_LOAD_TESTS=1`; `TAHSIL_LOAD_RENEWALS` sets the number of renewals (1,000 by
  default):

  ```sh
  TAHSIL_LOAD_TESTS=1 TAHSIL_LOAD_RENEWALS=5000 dotnet test --project tests/Tahsil.Server.Tests -c Release \
    --filter-class "*LoadTests"
  ```

  Each test appends its figures to `load-results.txt` next to the test binaries.
- API and page requests ran eight at a time after a warm-up of 50; renewals were 5,000 monthly subscriptions renewing
  at the same instant, driven by the workers on one instance.
- The image size is the base image (`mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra`) plus the published
  application (46 MB); the download size is their compressed layers.
- Idle memory is the resident size of the server a minute after startup and a few requests, with
  `DOTNET_PROCESSOR_COUNT=2`, as in a two-core container.

## What to keep in mind

- **Providers set the real pace of renewals.** With iyzico, Stripe, PayTR or Craftgate, each charge waits for the
  provider. The payment worker charges `Tahsil:Workers:PaymentConcurrency` invoices at once per instance (4 by default);
  raise it only within what your provider allows, counting every instance.
- **Garbage collection.** Tahsil runs with workstation garbage collection, which keeps an idle instance small and meets
  the targets above. On instances with many cores and a high request rate, `DOTNET_gcServer=1` can raise throughput at
  the cost of memory.
- **The image is above its size target** when unpacked, because the base image ships ICU and time zone data, which
  Turkish formatting and billing time zones need. What a host downloads is about 85 MB.
- **The database is the shared resource.** Every instance works through PostgreSQL; watch its CPU, connections and the
  `tahsil.worker.lag` metric (see [deployment](deployment.md)) before adding instances.
