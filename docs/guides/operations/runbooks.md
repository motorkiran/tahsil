# Runbooks

What to do when something goes wrong, or when a routine but delicate change is due. Each runbook says how you notice,
what to check, and what to do.

## A payment stuck awaiting the provider

**You notice** a payment in `processing` for long, or an attention item about it (after seven days without an answer,
or at once for providers that cannot be searched).

**Check** the payment in the admin panel (**Payments**): its reference, connection, history and what the provider
answered so far. Look it up at the provider by that reference.

**Do**: if the provider did charge it, resolve it as succeeded with the provider's ID as evidence; the invoice is paid
and its events go out. If not, resolve it as failed; dunning takes over as for any failure. Never charge the
customer again by hand while a payment is unresolved.

## A provider connection failing

**You notice** merchant failures (`tahsil.dunning.merchant_failures`), attention items, or a failing connection test.

**Check** the connection in **Provider connections**: run the test, and read the failure code of the latest payments.
The usual causes: credentials changed or revoked at the provider; a permission the provider has not granted (charges
without 3D Secure, card storage, PayTR's permissions for direct form mode, the POS settings in Craftgate); the egress
address not registered with the provider; an outage on the provider's side.

**Do**: fix the cause, then run the connection test. Meanwhile, failures on your side are not counted against
customers: their invoices wait, and are retried once payments work again. To stop new checkouts on a connection while
you investigate, **Pause checkouts** on it.

## Rotate provider credentials

1. Create new credentials at the provider, keeping the old ones valid.
2. Update the connection with them: `PATCH /api/v1/provider-connections/{key}` with the new `credentials`, or the
   provisioning document with the new configuration values.
3. Run the connection test, and **Run a test payment** if you want the whole path confirmed.
4. Revoke the old credentials at the provider.

## Rotate the master key

1. Generate a new key: `openssl rand -base64 32`.
2. Configure it as the current key with a new ID, and keep the old one as previous:

   ```sh
   Tahsil__Security__MasterKey=<new key>
   Tahsil__Security__MasterKeyId=2
   Tahsil__Security__PreviousMasterKeys__1=<old key>
   ```

3. Deploy to every instance. Everything still decrypts; new secrets are encrypted with the new key.
4. Run `dotnet Tahsil.Server.dll reencrypt` once, with the same configuration: it rewrites every secret with the new
   key.
5. Check that Tahsil works (connection tests, a webhook, the admin panel), back up the new key, then remove
   `PreviousMasterKeys` and deploy again.

## A leaked API key or webhook secret

- **API key**: revoke it at once (**API keys** in the admin panel, or `POST /api/v1/api-keys/{id}/revoke`); it stops
  working immediately. Create a new one for the application. Then read the audit log filtered by the key's ID (actor)
  for what it was used for.
- **Webhook secret**: rotate it (**Webhooks**, or `POST /api/v1/webhooks/endpoints/{id}/secret`). For 24 hours Tahsil
  signs with both secrets, so give the application the new one within that time; the old one stops being used after.

## Webhooks failing at the application

**You notice** an endpoint failing in **Webhooks**, or the application missing events.

**Check** the deliveries of the endpoint: the status and body the application answered.

**Do**: fix the application, then redeliver the failed deliveries; Tahsil also keeps retrying for about 45 hours.
Applications must handle each `webhook-id` once, so redelivering what they already handled is safe.

## Kimlik out of sync

**You notice** an attention item about the Kimlik sync, or a customer whose access does not match their subscription.

**Check** the subscription's page in the admin panel: its Kimlik state shows when it was last synced and the error
since. Check the connector's settings and that Kimlik is reachable, and Kimlik's own logs for the service client.

**Do**: once the cause is fixed, the next sync of the subscription, its next change or the reconciliation (every six
hours, and when an instance that runs the workers starts) puts Kimlik right. Drift that Kimlik reported is in the
audit log as `kimlik.drift_corrected`.

## Restore from a backup

1. Restore the database, and make sure the master key of that time is configured.
2. Start the instances with `Tahsil__Workers__Enabled=false`, so that nothing is charged yet.
3. In the admin panel, read the attention list and the payments in `processing`: these were in flight when the backup
   was taken, and Tahsil does not know how they ended.
4. Enable the workers. Reconciliation asks the providers about every payment and refund in flight and applies what
   they say; what they cannot answer becomes an attention item.
5. Webhooks of events after the backup were already sent before the failure; applications see the same `webhook-id`
   again only for events that are delivered again.
