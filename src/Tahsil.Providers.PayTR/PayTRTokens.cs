using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.PayTR;

/// <summary>
/// PayTR's tokens (PTR-4): Base64 of HMAC-SHA256 keyed with the merchant key over the exact strings posted, each
/// message ending with the merchant salt where PayTR puts it. Neither the key nor the salt is ever sent.
/// </summary>
internal static class PayTRTokens
{
    public static string IFrame(
        string key, string salt, string merchantId, string userIp, string merchantOid, string email, string paymentAmount, string basket, string noInstallment,
        string maxInstallment, string currency, string testMode) =>
        Sign(key, merchantId + userIp + merchantOid + email + paymentAmount + basket + noInstallment + maxInstallment + currency + testMode + salt);

    public static string Direct(
        string key, string salt, string merchantId, string userIp, string merchantOid, string email, string paymentAmount, string paymentType, string installmentCount,
        string currency, string testMode, string non3d) =>
        Sign(key, merchantId + userIp + merchantOid + email + paymentAmount + paymentType + installmentCount + currency + testMode + non3d + salt);

    public static string Notification(string key, string salt, string merchantOid, string status, string totalAmount) =>
        Sign(key, merchantOid + salt + status + totalAmount);

    public static string Refund(string key, string salt, string merchantId, string merchantOid, string returnAmount) =>
        Sign(key, merchantId + merchantOid + returnAmount + salt);

    public static string Status(string key, string salt, string merchantId, string merchantOid) => Sign(key, merchantId + merchantOid + salt);

    public static string CardList(string key, string salt, string utoken) => Sign(key, utoken + salt);

    public static string CardDelete(string key, string salt, string ctoken, string utoken) => Sign(key, ctoken + utoken + salt);

    private static string Sign(string key, string message) => Hmac.Base64(HmacAlgorithm.Sha256, key, message);
}
