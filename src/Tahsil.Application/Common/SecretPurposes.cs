namespace Tahsil.Application.Common;

/// <summary>
/// Purposes of encrypted secrets. They feed the key derivation, so changing a value makes existing ciphertext
/// unreadable. The owner (associated data) of each is noted.
/// </summary>
public static class SecretPurposes
{
    /// <summary>Provider credentials; owner: the connection.</summary>
    public const string ConnectionCredentials = "connections.credentials";

    /// <summary>Provider card tokens; owner: the payment method.</summary>
    public const string PaymentMethodToken = "payment_methods.token";

    /// <summary>Webhook signing secrets; owner: the endpoint.</summary>
    public const string WebhookEndpointSecret = "webhooks.endpoint_secret";

    /// <summary>Raw provider notification bodies; owner: the notification.</summary>
    public const string ProviderNotificationRawBody = "provider_notifications.raw_body";

    /// <summary>Tax IDs such as TCKN; owner: the customer, or the invoice inside its snapshot.</summary>
    public const string CustomerTaxId = "customers.tax_id";

    /// <summary>Checkout page tokens, kept to give a session's URL again; owner: the session.</summary>
    public const string CheckoutToken = "checkout_sessions.token";

    /// <summary>The HMAC key of hosted invoice links; no owner.</summary>
    public const string InvoiceLinkKey = "invoices.link_key";

    /// <summary>The ASP.NET Core Data Protection key ring; no owner.</summary>
    public const string DataProtectionKeyRing = "dataprotection.keyring";
}
