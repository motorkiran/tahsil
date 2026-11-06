# Going live

A checklist before Tahsil takes real payments. Go through it on the Live mode installation itself.

## The installation

- [ ] A Live mode installation (`Tahsil:Server:Mode=Live`) with its own database and its own master key, never the
      Test mode one's. Live mode refuses the public development key and plain HTTP.
- [ ] `PublicUrl` on HTTPS, the reverse proxy in `TrustedProxies` (see [deployment](deployment.md)).
- [ ] The billing time zone set (`Tahsil:Billing:TimeZone`); it must not change later.
- [ ] Backups with point-in-time recovery, a restore tried once, and the master key backed up apart from them.
- [ ] Monitoring and the alerts of [deployment](deployment.md#monitoring); `Tahsil:Notifications:OperatorEmail` set.

## The merchant

- [ ] The merchant's identity in `Tahsil:Merchant` (legal name, address, MERSİS number, tax office and number, KEP
      address, phone, email): checkout pages, documents and emails print it.
- [ ] The legal documents: the starter pre-information form and distance sales contract are a starting point only.
      Have a lawyer review them, or configure your own (`Tahsil:Checkout:LegalDocuments`), in both languages.
- [ ] Your own site has the pages Turkish e-commerce law asks of it, such as contact details and the purchase process,
      and the business is registered with ETBİS.

## Payments

- [ ] Production connections for your currencies, each tested, with the provider's approvals in place: charges
      without 3D Secure and card storage for renewals; PayTR's three permissions for direct form mode; the bank POS
      settings in Craftgate. See the [provider guides](../providers/README.md).
- [ ] The notification URL of each connection registered with its provider.
- [ ] **Run a test payment** on each connection with a real card, and see it refunded.
- [ ] The egress address registered with providers that ask for one.

## Email

- [ ] SMTP set (`Tahsil:Email`), with a sender domain that has SPF, DKIM and DMARC.
- [ ] Every email read in Turkish and in English, with your branding (`Tahsil:Branding`); overrides in
      `Tahsil:Notifications` if you changed the texts.

## Your application

- [ ] A Live mode API key with only the permissions the application uses, stored in its secret store.
- [ ] Its webhook endpoints, with their secrets, handling each `webhook-id` once.
- [ ] Legal invoices: the application issues the e-Fatura or e-Arşiv invoice with its integrator from
      `invoice.paid`, and records its number on Tahsil's invoice (`legalInvoice`), so the customer's documents show it.

## Administration

- [ ] The admin panel signs in through your OpenID Connect provider (`Tahsil:Admin:Oidc`), such as Kimlik, with MFA
      required (the default) and the panel's roles mapped from yours.
- [ ] `DevelopmentSignIn` off (Live mode refuses it anyway), and the panel served only by an internal instance.
