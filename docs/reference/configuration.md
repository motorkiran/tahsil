# Configuration reference

<!-- Generated from the options classes by ReferenceDocsTests; do not edit. Run the tests with TAHSIL_UPDATE_SNAPSHOTS=1 after changing them. -->

Tahsil reads its configuration at startup, from `appsettings.json`, then from environment variables, which win.
A setting such as `Tahsil:Server:PublicUrl` is the environment variable `Tahsil__Server__PublicUrl`; an item of a
list is numbered (`Tahsil__Dunning__RetrySchedule__0`), and an entry of a dictionary is named by its key. Durations
are written `d.hh:mm:ss`, such as `00:05:00` for five minutes or `3.00:00:00` for three days. Invalid settings stop
Tahsil at startup with a message that names them. The admin panel's Settings page shows the values an installation
runs with, secrets masked.

The database is `ConnectionStrings:Tahsil` (`ConnectionStrings__Tahsil`), a PostgreSQL connection string.

## `Tahsil:Admin`

Admin panel settings from the `Tahsil:Admin` configuration section.

| Setting | Default | Description |
|---|---|---|
| `DevelopmentSignIn` | `false` | Offers "Sign in as developer" with the admin role. Only for Test mode in the Development environment; Tahsil refuses to start with it anywhere else. |
| `Enabled` | `true` | Serves the panel on this instance; turn it off on public instances so that only an internal one serves it. |
| `Oidc:AccessTokenAudience` | – | The audience access tokens must have when the role comes from them, such as `tahsil`. |
| `Oidc:AccountUrl` | – | Where people enable a second factor, shown when one is missing; Kimlik's is `/account`. |
| `Oidc:Authority` | – | The OpenID Connect provider administrators sign in with, such as Kimlik's URL. |
| `Oidc:ClientId` | – | The panel's client ID at the provider. |
| `Oidc:ClientSecret` | – | The panel's client secret at the provider. |
| `Oidc:Organization` | – | For Kimlik: the organization whose sign-in rules apply, sent as `organization`. |
| `Oidc:RequireMfa` | `true` | Denies sessions whose ID token's `amr` lacks `mfa`. |
| `Oidc:RoleClaim` | `roles` | The claim that carries the administrator's roles. |
| `Oidc:RoleMappings` | – | Provider role values and the panel role each grants, such as `tahsil-admin` → `admin`. |
| `Oidc:RoleSource` | `IdToken` | Where the roles are read: the ID token, the userinfo endpoint or the access token. |
| `Oidc:Scopes` | `openid profile email` | Space-separated; `openid profile email` by default. |
| `SessionLifetime` | `08:00:00` | How long an admin session lasts at most, from sign-in. |

## `Tahsil:Audit`

Audit trail settings from the `Tahsil:Audit` configuration section.

| Setting | Default | Description |
|---|---|---|
| `RetentionPeriod` | `3650.00:00:00` | How long audit events are kept; ten years by default, the longest Turkish record-keeping period. |

## `Tahsil:Billing`

The billing time zone and other settings of the billing engine.

| Setting | Default | Description |
|---|---|---|
| `DraftPeriod` | `01:00:00` | How long a cycle invoice stays a draft that items can join before it finalizes itself; may be zero. |
| `TimeZone` | `UTC` | The IANA time zone of calendar arithmetic, invoice numbering years and printed dates; Turkish merchants set `Europe/Istanbul`. |

## `Tahsil:Branding`

Look of the hosted pages and emails, from the `Tahsil:Branding` configuration section.

| Setting | Default | Description |
|---|---|---|
| `LogoUrl` | – | Absolute URL of a logo shown above the pages and in emails; the product name is shown when unset. |
| `PrimaryColor` | `#0f766e` | Accent color as `#rrggbb`. |
| `ProductName` | `Tahsil` | The name shown on the pages and in emails. |
| `StylesheetPath` | – | Path of a CSS file that the hosted pages load after their own styles, such as one mounted into the container. It is read once, so a change takes a restart. |

## `Tahsil:Checkout`

From the `Tahsil:Checkout` configuration section.

From the `Tahsil:Checkout` section: the page's consents and pay button.

| Setting | Default | Description |
|---|---|---|
| `ImmediateStartConsent:Enabled` | `true` | Whether the checkout shows the box. |
| `ImmediateStartConsent:Text` | – | The checkbox text per locale; Tahsil's own when omitted. |
| `LegalDocuments` | – | The legal documents of the checkout; the starter pre-information form and distance sales contract when none are configured. |
| `LegalDocuments:{n}:CheckboxText` | – | The checkbox text per locale; a text for the document's type when omitted. |
| `LegalDocuments:{n}:CustomerTypes` | – | The customer types it applies to; both when empty. |
| `LegalDocuments:{n}:Key` | – | The document's key, such as `distance-sales`, recorded with each acceptance. |
| `LegalDocuments:{n}:Modes` | – | The session modes that show it; `payment` and `subscription` when empty. |
| `LegalDocuments:{n}:RequiresAcceptance` | – | Whether the customer must tick its checkbox to pay; a notice such as the privacy notice is only linked. |
| `LegalDocuments:{n}:Template` | – | A Liquid template per locale: a file path, or a starter such as `starter:pre-information`. |
| `LegalDocuments:{n}:Title` | – | The title per locale. |
| `LegalDocuments:{n}:Type` | – | `pre_information`, `distance_sales_contract`, `subscription_contract`, `privacy_notice` or `custom`. |
| `LegalDocuments:{n}:Url` | – | Instead of a template, a page per locale that the checkout links to. |
| `LegalDocuments:{n}:Version` | – | Changes whenever the content does, so earlier acceptances stay provable. |
| `MarketingConsent` |  | An optional marketing consent box, off by default; never a condition of the purchase. |
| `MarketingConsent:Enabled` | `false` | Whether the checkout shows the box. |
| `MarketingConsent:Text` | – | The checkbox text per locale; Tahsil's own when omitted. |
| `MaxAttemptsPerAddressPerHour` | `10` | Attempts per network (an IPv4 address or an IPv6 /64) per hour, across sessions. |
| `MaxFailedAttemptsPerCustomerPerDay` | `10` | Failed attempts per customer per day. |
| `MaxPaymentAttempts` | `5` | Payment attempts per session. |
| `PayButtonText` | – | The pay button's text per locale and mode, such as `PayButtonText:tr:payment`; `{amount}` is the total. |

## `Tahsil:Database`

Database settings from the `Tahsil:Database` configuration section. The connection string itself is read from `ConnectionStrings:Tahsil`.

| Setting | Default | Description |
|---|---|---|
| `MigrateOnStartup` | `true` | Applies pending migrations when the server starts. Disable it when this runs as a separate deployment step with the `migrate` command. |

## `Tahsil:Dunning`

| Setting | Default | Description |
|---|---|---|
| `CancelSubscription` | `true` | Whether the final action cancels the subscription; otherwise it stays past due for the operator. |
| `EmailEveryRetry` | `false` | Whether every failed retry is emailed, not only the first failure. |
| `FinalReminderBefore` | `1.00:00:00` | How long before the last retry the customer gets the last reminder. |
| `InvoiceAction` | `leave_open` | What the final action does with the invoice: `leave_open` (the default), `mark_uncollectible` or `void`. |
| `RetrySchedule` | – | The retries, as offsets from the invoice's first customer failure; 1, 3, 5 and 7 days when not set. Starts empty: configuration binding would add to defaults instead of replacing them. |

## `Tahsil:Email`

Email delivery settings from the `Tahsil:Email` configuration section.

| Setting | Default | Description |
|---|---|---|
| `FromAddress` | – | Sender address. Required to send email. |
| `FromName` | – | Sender display name; the merchant's name when unset. |
| `Smtp:Host` | – | The SMTP server. |
| `Smtp:Password` | – | The SMTP password. |
| `Smtp:Port` | `587` | The SMTP port; 587 with STARTTLS by default. |
| `Smtp:Security` | `Auto` | `Auto` uses TLS when the server offers it (implicit TLS on port 465); `None` only for local servers such as Mailpit. |
| `Smtp:Username` | – | The SMTP user, when the server asks for one. |

## `Tahsil:Invoices`

| Setting | Default | Description |
|---|---|---|
| `BankTransfer` | – | Bank accounts per currency for transfers, shown on hosted invoice pages. |
| `BankTransfer:{key}:AccountHolder` | – | Whose account it is, as the bank knows them. |
| `BankTransfer:{key}:Bank` | – | The bank's name. |
| `BankTransfer:{key}:Iban` | – | The account's IBAN. |
| `DefaultDaysUntilDue` | `14` | Days from finalization to the due date of `send_invoice` invoices without their own. |
| `DocumentTitle` | – | The title of hosted invoice pages per locale and status (`open`, `paid`…), replacing the default titles. |
| `LinkKeyVersion` | `1` | The version of the hosted invoice links' key; a new version invalidates every link. |
| `NumberPrefix` | `INV` | The first part of invoice numbers, as in `INV-2026-000001`. |
| `Reminders` | – | When invoices sent to customers are reminded of, as offsets from the start of their due day: 3 days before, on the day, and 3 and 7 days after when not set. Starts empty, since binding adds to a list's defaults. |

## `Tahsil:Kimlik`

The Kimlik connector: Tahsil's service client in Kimlik and how subscriptions are mirrored.

| Setting | Default | Description |
|---|---|---|
| `Authority` | – | Kimlik's public URL, with a trailing slash. |
| `ClientId` | – | Tahsil's service client in Kimlik. |
| `ClientSecret` | – | The service client's secret. |
| `Enabled` | `false` | Turns the Kimlik connector on. |
| `PastDueAccess` | `keep_until_final_action` | Whether a past-due subscription keeps its Kimlik plan until dunning's final action. |
| `ReconcileInterval` | `06:00:00` | How often every mirrored subscription is synced again. |
| `SyncBuffer` | `1.00:00:00` | Added to the access end Tahsil writes to Kimlik, so a late renewal does not cut access. |
| `WebhookSecret` | – | The `whsec_…` secret of the endpoint registered in Kimlik for its webhooks. |

## `Tahsil:Merchant`

The merchant's identity: shown in the footer of hosted pages, used by legal documents, and printed on invoices as the seller.

| Setting | Default | Description |
|---|---|---|
| `Address` | – | The registered address. |
| `Email` | – | The address customers can write to. |
| `KepAddress` | – | The registered electronic mail (KEP) address. |
| `LegalName` | – | The registered name, such as `Acme Yazılım A.Ş.`. |
| `LogoUrl` | – | The merchant's logo, when it differs from the branding's. |
| `MersisNumber` | – | The MERSİS number, printed on the pages and documents. |
| `Phone` | – | The phone number customers can call. |
| `TaxNumber` | – | The tax number (VKN). |
| `TaxOffice` | – | The tax office. |
| `TradeName` | – | The name customers know, such as `Acme`. |
| `Website` | – | The merchant's website. |

## `Tahsil:Notifications`

From the `Tahsil:Notifications` configuration section.

| Setting | Default | Description |
|---|---|---|
| `CardExpiringNoticeDays` | `30` | Days before a card used by a subscription expires that the customer is told. |
| `OperatorEmail` | – | Where operator emails go: attention items when they appear, and a daily digest of those still open. |
| `TemplatesPath` | – | A directory whose `.liquid` files replace the built-in templates of the same name. |
| `TrialEndingNoticeDays` | `3` | Days before a trial's end that the customer is reminded of it. |
| `Types` | – | Email types by name, such as `order_confirmation`; every type is on unless turned off. |
| `Types:{key}:Enabled` | – | Whether emails of this type are sent. |
| `UpcomingRenewalNoticeDays` | year, month, week, day | Days before a renewal that the customer is reminded of it, by the prices' interval: 14 for yearly subscriptions by default, 0 (none) for the others. |

## `Tahsil:Outbox`

Delivery settings from the `Tahsil:Outbox` configuration section.

| Setting | Default | Description |
|---|---|---|
| `BatchSize` | `20` | How many messages the dispatcher takes at once. |
| `MaxAttempts` | `10` | Attempts before a message is marked as failed. Retries back off exponentially, up to an hour apart. |
| `PollingInterval` | `00:00:05` | How often pending messages are looked for. Messages written by this instance are picked up as soon as their transaction commits; polling covers other instances and retries. |
| `RetentionPeriod` | `7.00:00:00` | How long delivered messages are kept before they are deleted. |

## `Tahsil:Payments`

| Setting | Default | Description |
|---|---|---|
| `CustomerActionTimeout` | `00:30:00` | How long a customer has to finish a payment on a page before it is abandoned. |
| `NotFoundAfter` | `00:15:00` | How old a payment must be before a provider's "not found" counts as "never received". |
| `ReconcileAfter` | `00:02:00` | When a charge without an answer is first reconciled. |

## `Tahsil:Portal`

From the `Tahsil:Portal` section.

| Setting | Default | Description |
|---|---|---|
| `CompanyCancellation` | `period_end` | How company customers may cancel: `period_end` only, or `both` like individuals. |
| `DowngradeTiming` | `period_end` | When a downgrade takes effect: `period_end`, or `now` with a credit for the unused time. |
| `EmailLinkLifetime` | `7.00:00:00` | How long the portal link of an email works. |
| `Features` | – | Turns features on or off by name; those not named keep their defaults (`PortalFeatures`). |
| `Products` | – | The products, and of each the prices, a customer may switch among; none offers no other plan. |
| `Products:{n}:Prices` | – | The prices of the product that the portal offers, by lookup key; all its prices when empty. |
| `Products:{n}:Product` | – | The product, by key, whose prices customers may change between. |
| `QuantityLimits` |  | The quantities a customer may choose; the same minimum and maximum leaves quantities alone. |
| `QuantityLimits:Maximum` | `1` | The most units a customer may choose. |
| `QuantityLimits:Minimum` | `1` | The fewest units a customer may choose. |
| `RetentionCoupon` | – | The key of a coupon offered to keep a subscription that is being canceled; none by default. |
| `SessionLifetime` | `01:00:00` | How long a session of the API lasts. |

## `Tahsil:Providers`

Provider settings from the `Tahsil:Providers` configuration section.

| Setting | Default | Description |
|---|---|---|
| `SmokeTestAmount` | `100` | What an operator's test payment charges, in minor units of its currency; the provider's least amount when that is more. |

## `Tahsil:Provisioning`

From the `Tahsil:Provisioning` configuration section.

| Setting | Default | Description |
|---|---|---|
| `FilePath` | – | A provisioning document applied whenever the database is prepared, such as at startup. |

## `Tahsil:RateLimits`

Rate limits from the `Tahsil:RateLimits` configuration section. Limits are per instance.

| Setting | Default | Description |
|---|---|---|
| `ApiBurst` | `50` | API requests one API key may make at once after a quiet period. |
| `ApiRequestsPerSecond` | `25` | API requests per second of one API key, on average. |
| `HostedPageRequestsPerMinute` | `120` | Hosted page requests per minute from one network address (IPv6 by /64). |
| `ProviderNotificationsPerMinute` | `600` | Provider notifications per minute from one network address. |
| `UsageBatchesPerSecond` | `10` | Batches of usage events per second of one API key, which is also what it may send at once; apart from its other requests. |

## `Tahsil:Refunds`

| Setting | Default | Description |
|---|---|---|
| `DailyLimitPerActor` | – | What one API key or administrator may refund per day in each currency, in minor units; no limit for currencies not listed. |

## `Tahsil:Security`

Security settings from the `Tahsil:Security` configuration section.

| Setting | Default | Description |
|---|---|---|
| `MasterKey` | – | Base64-encoded 256-bit key that encrypts secrets at rest. Generate one with `openssl rand -base64 32` and back it up: encrypted data cannot be recovered without it. |
| `MasterKeyId` | `1` | The ID of `MasterKey`, 1 to 255, written into everything it encrypts. Give a new key a new ID when rotating, and keep the old one in `PreviousMasterKeys` until `reencrypt` has run. |
| `PreviousMasterKeys` | – | Earlier master keys by ID, used only to decrypt what they encrypted. |

## `Tahsil:Server`

Host settings from the `Tahsil:Server` configuration section.

| Setting | Default | Description |
|---|---|---|
| `DefaultLocale` | `tr` | The locale of customers that have none, `tr` or `en`. |
| `Mode` | `Test` | `Test` until the installation is ready to take real payments. |
| `PublicUrl` | – | The public base URL of this installation, such as `https://billing.example.com/`. Every link Tahsil produces (hosted pages, emails, provider notification and return URLs) starts with it, so it is never inferred from request headers. |
| `RequireHttps` | `true` | Requires HTTPS: the public URL must use it, and browsers are told to use nothing else (HSTS). Only disable it for local development; behind a TLS-terminating proxy, list the proxy in `TrustedProxies` instead. Live mode requires it. |
| `TrustedProxies` | – | The reverse proxies in front of Tahsil, as addresses (`10.0.0.5`) or networks (`10.0.0.0/8`). From them only, `X-Forwarded-For` and `X-Forwarded-Proto` give the client's address and the scheme it used. List only proxies you run: the client's address feeds rate limits, consent records and provider requests. |

## `Tahsil:Subscriptions`

| Setting | Default | Description |
|---|---|---|
| `IncompleteExpiry` | `23:00:00` | How long an incomplete subscription waits for its first invoice to be paid. |
| `PriceIncreaseNotice` | `30.00:00:00` | How far ahead a merchant schedules a price increase for an individual customer. |

## `Tahsil:Usage`

Usage reporting settings from the `Tahsil:Usage` configuration section.

| Setting | Default | Description |
|---|---|---|
| `FutureTolerance` | `00:05:00` | How far ahead of the billing clock an event's timestamp may be. |
| `MaxBatchSize` | `1000` | The most events one request reports. |
| `RetentionPeriod` | `400.00:00:00` | How long events are kept after they happened, once their period is invoiced. |

## `Tahsil:Webhooks`

Delivery settings from the `Tahsil:Webhooks` configuration section.

| Setting | Default | Description |
|---|---|---|
| `AllowPrivateNetworks` | `false` | Lets endpoints on private networks receive webhooks, as when Tahsil and the application share an internal network. Off, webhooks only go to the public internet. |
| `BatchSize` | `20` | How many deliveries the sender takes at once. |
| `EndpointConcurrency` | `4` | Deliveries to one endpoint at a time, across a batch. |
| `PollingInterval` | `00:00:05` | How often due deliveries are looked for; new events on this instance are sent at once. |
| `RetentionPeriod` | `30.00:00:00` | How long the delivery log is kept. |
| `RetryDelays` | – | The waits before each retry of a failed delivery, after which it fails for good: by default 10 seconds, 1, 5 and 30 minutes, 2, 6, 12 and 24 hours, nine attempts over about 45 hours. |
| `SecretRotationOverlap` | `1.00:00:00` | How long a rotated secret keeps signing beside the new one. |
| `Timeout` | `00:00:10` | How long an endpoint has to answer an attempt. |

## `Tahsil:Workers`

Background worker settings from the `Tahsil:Workers` configuration section.

| Setting | Default | Description |
|---|---|---|
| `BatchSize` | `100` | The most items a worker processes before it looks at the time again. |
| `Enabled` | `true` | Runs the workers and periodic jobs on this instance. Any number of instances may run them; turn them off on instances that should only serve requests. |
| `Lease` | `00:05:00` | How long a worker that calls other services owns an item. If the instance dies, the item is due again once the lease runs out. |
| `PaymentConcurrency` | `4` | The most invoices one instance charges at once. Each instance charges this many; keep the sum across instances within the providers' rate limits. |
| `PollingInterval` | `00:00:10` | How often workers look for due items. Items this instance makes due are picked up at once; polling covers other instances and scheduled times. |
