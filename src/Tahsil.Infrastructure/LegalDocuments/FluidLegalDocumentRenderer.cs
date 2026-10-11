using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Fluid;
using Microsoft.Extensions.Hosting;
using Tahsil.Application.Checkout;

namespace Tahsil.Infrastructure.LegalDocuments;

/// <summary>
/// Renders legal documents with Fluid (Liquid), HTML-encoding the data. Starter templates are embedded in this
/// assembly (<c>starter:pre-information</c>); others are files, relative to the content root.
/// </summary>
internal sealed class FluidLegalDocumentRenderer(IHostEnvironment environment) : ILegalDocumentRenderer
{
    private static readonly FluidParser Parser = new();

    // Data is HTML-encoded, but letters such as ş and ₺ stay as they are: records keep the text the customer read.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);
    private static readonly TemplateOptions Options = CreateOptions();
    private readonly ConcurrentDictionary<string, (string Source, IFluidTemplate Template)> _templates = new(StringComparer.Ordinal);

    public async Task<string> RenderAsync(LegalDocumentOptions document, string locale, IReadOnlyDictionary<string, object?> model, CancellationToken cancellationToken)
    {
        var path = LegalDocumentOptions.InLocale(document.Template, locale)
            ?? throw new InvalidOperationException($"The legal document '{document.Key}' has no template; it is a link.");
        var context = new TemplateContext(model, Options);
        context.SetValue("locale", locale);
        context.SetValue("title", LegalDocumentOptions.InLocale(document.Title, locale));
        context.SetValue("version", document.Version);
        return await Load(path, locale).Template.RenderAsync(context, Encoder);
    }

    public string? SourceOf(LegalDocumentOptions document, string locale) =>
        LegalDocumentOptions.InLocale(document.Template, locale) is { } path ? Load(path, locale).Source : null;

    private static TemplateOptions CreateOptions()
    {
        var options = new TemplateOptions();
        options.MemberAccessStrategy.IgnoreCasing = false;
        return options;
    }

    private (string Source, IFluidTemplate Template) Load(string path, string locale) => _templates.GetOrAdd($"{path}|{locale}", _ =>
    {
        var source = ReadSource(path, locale);
        return Parser.TryParse(source, out var template, out var error)
            ? (source, template)
            : throw new InvalidOperationException($"The legal document template '{path}' is invalid: {error}");
    });

    private string ReadSource(string path, string locale)
    {
        if (path.StartsWith(LegalDocumentOptions.StarterPrefix, StringComparison.Ordinal))
        {
            var name = $"LegalDocuments.{path[LegalDocumentOptions.StarterPrefix.Length..]}.{locale}.liquid";
            using var stream = typeof(FluidLegalDocumentRenderer).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"There is no starter legal document '{path}' in '{locale}'.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        var file = Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path);
        return File.Exists(file) ? File.ReadAllText(file) : throw new InvalidOperationException($"The legal document template '{file}' does not exist.");
    }
}
