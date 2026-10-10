# Changelog

All notable changes to Tahsil are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/); until 1.0, minor versions may break compatibility.

## [0.1.0] - Unreleased

### Added

- The server foundation: configuration validated at startup, with Test and Live modes (Live mode refuses plain HTTP and the public development master key) and a `Tahsil-Mode: test` header and page banner in Test mode.
- PostgreSQL storage with automatic migrations, or the `migrate` command as a separate deployment step.
- Secrets encrypted with a master key that can be rotated: earlier keys stay configured for decryption, and the `reencrypt` command moves everything to the current key.
- Background workers that any number of instances share, with backoff and an attention report for items that keep failing, and an outbox that delivers side effects after their transaction commits.
- Hosted pages in Turkish and English with branding, a strict content security policy and a localized error page; API errors as RFC 9457 problem details with a `code` and a `traceId`.
- OpenTelemetry traces and metrics, `/health/live` and `/health/ready`, a container image (`dotnet publish -t:PublishContainer`) and a Compose file for local development.
- API keys (`ths_test_` and `ths_live_`, by the installation's mode) with permissions, an escalation guard, per-key rate limits and audit; `Idempotency-Key` on every `POST`, safe to retry even after a crash.
- The catalog: tax rates, meters, products and prices (one-time and recurring; per-unit, package and tiered; trials; lookup keys that can move to a new price), through the API and from a provisioning document applied at startup or through the API, with export.
- Customers with external IDs, upsert by external ID, search, TCKN and VKN validation, encrypted tax IDs, deletion or anonymization, and data export for KVKK requests.
- Payment provider connections: credentials validated against each provider's fields, stored encrypted and never returned; a connection test; which connection serves each currency; Test mode takes sandbox connections and Live mode production ones, and a fresh Test mode installation starts with a simulator connection for TRY, USD and EUR.
- A built-in payment simulator for Test mode: test cards for successes, declines, 3D Secure, timeouts and slow or failing refunds, a card page that refuses any other card number, and settings that make it behave like providers with fewer capabilities.
- The admin panel under `/admin`: sign-in through any OpenID Connect provider such as Kimlik (roles from the ID token, userinfo or access token; MFA required by default), or as the developer in Test mode during development; customers, products and prices, tax rates, API keys and the audit log.
