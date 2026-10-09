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
