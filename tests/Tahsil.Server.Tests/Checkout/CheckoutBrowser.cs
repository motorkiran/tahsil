using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Providers.Simulator;

namespace Tahsil.Server.Tests.Checkout;

/// <summary>A customer on the checkout page: the form with its anti-forgery token, the simulator's card page, the return.</summary>
internal static partial class CheckoutBrowser
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static HttpClient Open(WebApplicationFactory<Program> host) => host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static Dictionary<string, string> Form(string email, string name, IReadOnlyList<string> documents, bool immediateStart = false)
    {
        var form = new Dictionary<string, string> { ["Form.Email"] = email, ["Form.Name"] = name };
        foreach (var (index, document) in documents.Index())
        {
            form[$"Form.Documents#{index}"] = document;
        }

        if (immediateStart)
        {
            form["Form.ImmediateStart"] = "true";
        }

        return form;
    }

    /// <summary>Submits the form as a browser does: with the anti-forgery token of the page it just loaded.</summary>
    public static async Task<HttpResponseMessage> SubmitAsync(HttpClient browser, Uri url, Dictionary<string, string> form)
    {
        using var get = await browser.GetAsync(url, CancellationToken);
        var page = await ParseAsync(get);
        var fields = form.Select(pair => new KeyValuePair<string, string>(pair.Key.Split('#')[0], pair.Value)).ToList();
        fields.Add(new("__RequestVerificationToken", page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!));
        return await browser.PostAsync(url, new FormUrlEncodedContent(fields), CancellationToken);
    }

    /// <summary>The simulator's page token of the embed the submitted form shows.</summary>
    public static async Task<string> SimulatorTokenAsync(HttpResponseMessage submitted)
    {
        var page = await ParseAsync(submitted);
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, page.QuerySelector("#checkout-error")?.TextContent);
        return SimulatorToken().Match(page.QuerySelector("#payment iframe")!.GetAttribute("src")!).Groups["token"].Value;
    }

    /// <summary>
    /// Pays on the simulator's card page in the embed, ticking its own box to save the card where the connection asks,
    /// then follows the provider's return to Tahsil.
    /// </summary>
    public static async Task<HttpResponseMessage> PayOnTheEmbedAsync(
        WebApplicationFactory<Program> host, HttpClient browser, HttpResponseMessage submitted, string card, bool saveCard = false)
    {
        var token = await SimulatorTokenAsync(submitted);
        var result = await host.Services.GetRequiredService<SimulatorBank>().SubmitCardAsync(token, new CardEntryForm(card, "12/30", "123", saveCard), CancellationToken);
        return await browser.GetAsync(result.ShouldBeOfType<CardPageResult.Redirect>().Url, CancellationToken);
    }

    public static async Task<IHtmlDocument> ParseAsync(HttpResponseMessage response) =>
        await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(CancellationToken));

    [GeneratedRegex("simulator/pay/(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex SimulatorToken();
}
