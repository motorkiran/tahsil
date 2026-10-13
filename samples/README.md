# Samples

## Notes: selling a Kimlik plan through Tahsil

A single-page app and its API, with [Kimlik](https://github.com/motorkiran/kimlik) for sign-in and Tahsil for billing:

- The app (`Notes/wwwroot`) signs users in with Kimlik through [oidc-client-ts](https://github.com/authts/oidc-client-ts) and calls its API with their access token.
- The API (`Notes`) accepts Kimlik access tokens with `Kimlik.AspNetCore`. Everyone keeps notes; exporting them needs the `export_notes` feature, which only Kimlik's `pro` plan has (`RequireFeature("export_notes")`).
- **Upgrade to Pro** creates (or updates) the Tahsil customer of the Kimlik user, linked to it with `kimlikSubscriber`, and opens Tahsil's hosted checkout for the `notes-pro-monthly` price. The `notes-pro` product has the Kimlik plan `pro`: once the first payment succeeds, Tahsil gives the user Kimlik's `pro` plan, and the app gets it with its next token. Nothing in the app tells Kimlik about payments.
- **Manage billing** opens Tahsil's customer portal, where the user changes their card or cancels; when the subscription ends, Tahsil takes the plan back in Kimlik.

[`kimlik/provisioning.json`](kimlik/provisioning.json) declares what Kimlik needs: Tahsil's admin and service clients (with the roles of KML-8 and the connector's permissions), the Notes app and API, and the `free` and `pro` plans. [`Notes/tahsil-provisioning.json`](Notes/tahsil-provisioning.json) declares the product and price in Tahsil.

### Run it

You need Tahsil's repository and a clone of Kimlik's next to it (Kimlik's .NET packages are not on NuGet yet; the sample references its projects, see `Notes/Notes.csproj`).

```bash
# 1. Kimlik's image, from Kimlik's repository
(cd ../kimlik && dotnet publish src/Kimlik.Server -t:PublishContainer)

# 2. PostgreSQL, Mailpit and Kimlik on http://localhost:5280, with the provisioning file above
docker compose --profile kimlik up -d

# 3. The billing administrator and Tahsil's webhook endpoint in Kimlik; the endpoint's secret goes to Tahsil's user secrets
dotnet run samples/kimlik/setup.cs

# 4. Tahsil on http://localhost:5090, with the Kimlik connector and the admin sign-in through Kimlik
dotnet run --project src/Tahsil.Server --launch-profile kimlik
```

Sign in to the admin panel at http://localhost:5090/admin as `billing@tahsil.localhost` / `Billing-dev-Passw0rd!`, and create a Test mode API key under **API keys** with `tahsil.customers:write` and `tahsil.checkout:write`. Then:

```bash
dotnet user-secrets set Tahsil:ApiKey ths_test_… --project samples/Notes
dotnet run --project samples/Notes        # the sample on http://localhost:5174
```

Open http://localhost:5174, choose **Sign in** and create an account (the verification email arrives in Mailpit at http://localhost:8026). **Export** is refused on the Free plan. **Upgrade to Pro** goes to Tahsil's checkout; pay with the simulator's card `4242 4242 4242 4242`. Back in the app, the plan turns to Pro and export works. **Manage billing** and cancel the subscription in the portal: at the end of the period, or at once, the plan goes back to Free.

### What else to try

- Kimlik's admin panel (http://localhost:5280, `admin@kimlik.localhost` / `development-only-password`): change the user's subscription there, and Tahsil restores it; delete the user, and Tahsil ends their subscription and keeps the customer.
- Tahsil's Test mode time travel (admin panel, **Test mode**): renewals extend the Kimlik subscription's period; a declined renewal keeps the plan until dunning's last attempt, then takes it.

### How the pieces reach each other

Kimlik delivers webhooks over plain HTTP only to `localhost`. In the Compose profile, a relay runs in Kimlik's container and passes its `localhost:5090` on to Tahsil on your machine, so Tahsil's endpoint in Kimlik is `http://localhost:5090/webhooks/kimlik`, and Kimlik runs with `Kimlik__Webhooks__AllowPrivateNetworks=true`. In production, Kimlik reaches Tahsil at its public HTTPS address and needs neither.
