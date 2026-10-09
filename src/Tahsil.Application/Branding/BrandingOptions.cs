using System.ComponentModel.DataAnnotations;

namespace Tahsil.Application.Branding;

/// <summary>Look of the hosted pages and emails, from the <c>Tahsil:Branding</c> configuration section.</summary>
public sealed class BrandingOptions
{
    public const string SectionName = "Tahsil:Branding";

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string ProductName { get; set; } = "Tahsil";

    /// <summary>Absolute URL of a logo shown above the pages and in emails; the product name is shown when unset.</summary>
    public Uri? LogoUrl { get; set; }

    /// <summary>Accent color as <c>#rrggbb</c>.</summary>
    [RegularExpression("^#[0-9a-fA-F]{6}$")]
    public string PrimaryColor { get; set; } = "#0f766e";

    /// <summary>
    /// Path of a CSS file that the hosted pages load after their own styles, such as one mounted into the container. It
    /// is read once, so a change takes a restart.
    /// </summary>
    public string? StylesheetPath { get; set; }

    internal bool HasReadableStylesheet() => StylesheetPath is not { Length: > 0 } path || File.Exists(path);
}
