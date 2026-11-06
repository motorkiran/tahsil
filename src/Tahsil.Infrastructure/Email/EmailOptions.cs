using System.ComponentModel.DataAnnotations;

namespace Tahsil.Infrastructure.Email;

/// <summary>Email delivery settings from the <c>Tahsil:Email</c> configuration section (NTF-13).</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Tahsil:Email";

    /// <summary>Sender address. Required to send email.</summary>
    [EmailAddress]
    public string? FromAddress { get; set; }

    /// <summary>Sender display name; the merchant's name when unset.</summary>
    public string? FromName { get; set; }

    public SmtpOptions Smtp { get; set; } = new();

    internal bool IsConfigured => !string.IsNullOrWhiteSpace(Smtp.Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed class SmtpOptions
{
    /// <summary>The SMTP server.</summary>
    public string? Host { get; set; }

    /// <summary>The SMTP port; 587 with STARTTLS by default.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary>The SMTP user, when the server asks for one.</summary>
    public string? Username { get; set; }

    /// <summary>The SMTP password.</summary>
    public string? Password { get; set; }

    /// <summary><c>Auto</c> uses TLS when the server offers it (implicit TLS on port 465); <c>None</c> only for local servers such as Mailpit.</summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;
}

public enum SmtpSecurity
{
    Auto,
    StartTls,
    SslOnConnect,
    None,
}
