namespace Tahsil.Infrastructure.Security;

/// <summary>Security settings from the <c>Tahsil:Security</c> configuration section.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Tahsil:Security";

    public const int MasterKeySizeInBytes = 32;

    /// <summary>
    /// The master key of the development settings and the documentation. It is public, so Live mode refuses it.
    /// </summary>
    public const string DevelopmentMasterKey = "RS4WftirAh28WH2LxhF6vetp9iHyc2xfGSQou5e16R0=";

    /// <summary>
    /// Base64-encoded 256-bit key that encrypts secrets at rest. Generate one with
    /// <c>openssl rand -base64 32</c> and back it up: encrypted data cannot be recovered without it.
    /// </summary>
    public string MasterKey { get; set; } = string.Empty;

    internal static bool IsValidMasterKey(string? value)
    {
        Span<byte> buffer = stackalloc byte[MasterKeySizeInBytes + 1];
        return Convert.TryFromBase64String(value ?? string.Empty, buffer, out var length) && length == MasterKeySizeInBytes;
    }

    internal bool IsDevelopmentMasterKey() => MasterKey.Trim() == DevelopmentMasterKey;
}
