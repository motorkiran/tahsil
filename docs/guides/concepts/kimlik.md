# Kimlik

[Kimlik](https://github.com/motorkiran/kimlik) is the identity server Tahsil was built beside. With the connector on,
a Tahsil subscription becomes a Kimlik subscription: buying the Pro plan in Tahsil gives the user the Pro plan's
entitlements in Kimlik, and access ends when the subscription does.

## Setting it up

1. In Kimlik, create a service client for Tahsil with the subscription management permissions, and a webhook
   endpoint pointing to Tahsil's `/webhooks/kimlik`.
2. In Tahsil, set `Tahsil:Kimlik:Enabled`, `Authority`, `ClientId`, `ClientSecret` and the endpoint's
   `WebhookSecret`.
3. Give each product the Kimlik plan it grants (`kimlikPlan`, a plan key that Tahsil checks against Kimlik).
4. Link each customer to their Kimlik user or organization (`kimlikSubscriber` on the customer, or on the checkout
   session's customer details).

[samples/README.md](../../../samples/README.md) runs all of this locally with Compose.

## How subscriptions are mirrored

- Tahsil is the source of truth for billing; Kimlik for who has access. Tahsil writes the plan, the status and the
  end of access to Kimlik after every change, through its outbox, so a Kimlik outage delays access changes but never
  loses them; a reconciliation every six hours (`ReconcileInterval`) repairs anything that drifted.
- The end of access Tahsil writes is the period's end plus a day (`SyncBuffer`), so that a renewal a little late never
  cuts a paying customer off.
- A customer has at most one subscription with a Kimlik plan, since Kimlik keeps one subscription per subscriber.
- While a renewal is being recovered, the customer keeps their plan until [dunning](dunning.md)'s final action
  (`PastDueAccess`).
- When Kimlik deletes a user or an organization, Tahsil cancels its subscription.

Administrators can also sign in to Tahsil's admin panel through Kimlik (`Tahsil:Admin:Oidc`), with panel roles
mapped from Kimlik roles (`tahsil-admin`, `tahsil-operator` and `tahsil-viewer` by default;
`samples/kimlik/provisioning.json` declares them with the panel's client).

The panel refuses sessions without a second factor (`Tahsil:Admin:RequireMfa`, on by default) and sends the
administrator to Kimlik's account page to set one up. Kimlik asks for a second factor only from some users unless told
otherwise, so set `Kimlik__Mfa__RequireForEveryone=true` on the Kimlik that signs administrators in.
