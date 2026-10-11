using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Fluid;
using Microsoft.Extensions.Options;
using Tahsil.Application.Branding;
using Tahsil.Application.Checkout;
using Tahsil.Application.Notifications;

namespace Tahsil.Infrastructure.Email;

/// <summary>
/// Renders the email templates embedded in this assembly (NTF-8): a template sets three captured blocks, <c>subject</c>,
/// <c>html</c> (wrapped in the shared layout with the branding and the merchant's identity) and <c>text</c>.
/// </summary>
internal sealed class FluidEmailTemplateRenderer(IOptions<BrandingOptions> branding, IOptions<MerchantOptions> merchant, IOptions<NotificationOptions> notifications)
    : IEmailTemplateRenderer
{
    private static readonly FluidParser Parser = new();
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);
    private readonly ConcurrentDictionary<string, IFluidTemplate?> _templates = new(StringComparer.Ordinal);

    public async Task<RenderedEmail> RenderAsync(string templateName, string locale, IReadOnlyDictionary<string, object?> model, CancellationToken cancellationToken)
    {
        var source = Load($"{templateName}.{locale}.liquid") ?? Load($"{templateName}.en.liquid")
            ?? throw new InvalidOperationException($"The email template '{templateName}' does not exist.");
        var values = new Dictionary<string, object?>(model, StringComparer.Ordinal)
        {
            ["product"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = branding.Value.ProductName,
                ["color"] = branding.Value.PrimaryColor,
                ["logo_url"] = branding.Value.LogoUrl?.AbsoluteUri,
            },
            ["seller"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = merchant.Value.DisplayName,
                ["legal_name"] = merchant.Value.LegalName,
                ["address"] = merchant.Value.Address,
                ["email"] = merchant.Value.Email,
            },
            ["locale"] = locale,
        };

        // Rendered twice: HTML-encoded for the HTML part, verbatim for the subject and the text part. The contexts are
        // not isolated, so the captured blocks can be read after rendering.
        var html = new TemplateContext(values);
        await source.RenderAsync(html, Encoder, isolateContext: false);
        var text = new TemplateContext(values);
        await source.RenderAsync(text, NullEncoder.Default, isolateContext: false);

        var layout = new TemplateContext(values);
        layout.SetValue("content", html.GetValue("html").ToStringValue());
        return new RenderedEmail(
            text.GetValue("subject").ToStringValue().Trim(),
            await Load("_layout.html.liquid")!.RenderAsync(layout, Encoder),
            text.GetValue("text").ToStringValue().Trim());
    }

    /// <summary>Parses a template, or says why it is invalid (NTF-9: overrides are checked at startup).</summary>
    internal static IFluidTemplate Parse(string name, string source) =>
        Parser.TryParse(source, out var template, out var error) ? template : throw new InvalidOperationException($"The email template '{name}' is invalid: {error}");

    /// <summary>A template by file name: the operator's override in <c>Tahsil:Notifications:TemplatesPath</c>, else the built-in one.</summary>
    private IFluidTemplate? Load(string name) => _templates.GetOrAdd(name, resource =>
    {
        if (notifications.Value.TemplatesPath is { } directory && Path.Combine(directory, resource) is var file && File.Exists(file))
        {
            return Parse(file, File.ReadAllText(file));
        }

        using var stream = typeof(FluidEmailTemplateRenderer).Assembly.GetManifestResourceStream($"Email.{resource}");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return Parse(resource, reader.ReadToEnd());
    });
}
