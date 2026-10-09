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

    /// <summary>
    /// The ID of <see cref="MasterKey"/>, 1 to 255, written into everything it encrypts. Give a new key a new ID when
    /// rotating, and keep the old one in <see cref="PreviousMasterKeys"/> until <c>reencrypt</c> has run.
    /// </summary>
    public int MasterKeyId { get; set; } = 1;

    /// <summary>Earlier master keys by ID, used only to decrypt what they encrypted.</summary>
    public Dictionary<int, string> PreviousMasterKeys { get; set; } = [];

    internal static bool IsValidMasterKey(string? value)
    {
        Span<byte> buffer = stackalloc byte[MasterKeySizeInBytes + 1];
        return Convert.TryFromBase64String(value ?? string.Empty, buffer, out var length) && length == MasterKeySizeInBytes;
    }

    internal static bool IsValidKeyId(int id) => id is >= 1 and <= byte.MaxValue;

    internal bool IsDevelopmentMasterKey() => MasterKey.Trim() == DevelopmentMasterKey;

    internal bool HasValidPreviousKeys() =>
        PreviousMasterKeys.All(pair => IsValidKeyId(pair.Key) && pair.Key != MasterKeyId && IsValidMasterKey(pair.Value));

    /// <summary>Every configured key by ID, the current one included.</summary>
    internal Dictionary<byte, byte[]> DecodeKeys()
    {
        var keys = PreviousMasterKeys.ToDictionary(pair => (byte)pair.Key, pair => Convert.FromBase64String(pair.Value));
        keys[(byte)MasterKeyId] = Convert.FromBase64String(MasterKey);
        return keys;
    }
}
