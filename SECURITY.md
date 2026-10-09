# Security policy

Tahsil handles payments, so security reports get priority.

## Reporting a vulnerability

Please do not open a public issue. Report vulnerabilities privately through GitHub: **Security → Report a vulnerability** on [motorkiran/tahsil](https://github.com/motorkiran/tahsil/security/advisories/new).

Include the version or commit, the configuration that matters, the steps to reproduce and the impact you see. You will get an answer within a few working days; once a fix is released, the advisory is published with credit to the reporter unless they prefer otherwise.

## Supported versions

Until 1.0, only the latest release receives security fixes.

## Scope

In scope: the Tahsil server, its hosted pages and admin panel, the provider adapters, and the `Tahsil.Client` and `Tahsil.AspNetCore` packages. Vulnerabilities in a payment provider's own service belong to that provider.
