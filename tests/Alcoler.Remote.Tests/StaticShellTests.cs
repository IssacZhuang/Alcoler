using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("api-gateway")]
public sealed class StaticShellTests
{
    private readonly GatewayHarness _harness;

    public StaticShellTests(GatewayHarness harness) => _harness = harness;

    [Fact]
    public async Task Root_ServesHtmlShell()
    {
        using var response = await _harness.Client.GetAsync(string.Empty);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<!doctype", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"view-pair\"", body);
        Assert.Contains("id=\"view-shell\"", body);
        Assert.Contains("class=\"sidebar\"", body);
        Assert.Contains("data-composer=\"draft\"", body);
        Assert.Contains("data-composer=\"session\"", body);
        Assert.Equal(2, body.Split("class=\"model-select\"").Length - 1);
        Assert.Equal(2, body.Split("class=\"effort-select\"").Length - 1);
        Assert.Equal(2, body.Split("class=\"slash-popup\"").Length - 1);
        Assert.Equal(2, body.Split("aria-autocomplete=\"list\"").Length - 1);
        Assert.Equal(2, body.Split("class=\"workflow-badge\"").Length - 1);
        Assert.Matches("class=\"pair-logo\"[^>]*>\\s*<img src=\"icons/favicon.svg\"", body);
        Assert.Matches("class=\"sb-brand\"><img class=\"sb-brand-icon\" src=\"icons/favicon.svg\"", body);
        Assert.Contains("class=\"sb-brand-icon\" src=\"icons/favicon.svg\" alt=\"Alcoler\"", body);
    }

    [Fact]
    public async Task Composers_DefaultToFullAccessAndAllowLocalImageSelection()
    {
        var body = await _harness.Client.GetStringAsync(string.Empty);
        Assert.Equal(2, Regex.Matches(body, @"<option value=""yolo"" selected>Full access</option>").Count);
        Assert.Equal(2, Regex.Matches(body, @"aria-label=""Attach images"" title=""Attach images""").Count);
        var inputs = Regex.Matches(body, @"<input class=""composer-file""[^>]*>");
        Assert.Equal(2, inputs.Count);
        foreach (Match input in inputs)
        {
            Assert.Contains("type=\"file\"", input.Value);
            Assert.Contains("accept=\"image/*\"", input.Value);
            Assert.Contains("multiple", input.Value);
            Assert.DoesNotContain("capture", input.Value);
        }
    }

    [Fact]
    public async Task MobileSessionStyles_KeepRowsAndTheirControlsCompact()
    {
        var css = await _harness.Client.GetStringAsync("styles.css");
        var start = css.IndexOf("@media (max-width: 899px)", StringComparison.Ordinal);
        var end = css.IndexOf("/* ---------- Desktop layout ---------- */", start, StringComparison.Ordinal);
        var mobile = css[start..end];
        Assert.Matches(@"(?s)\.sidebar \.session-group\s*\{[^}]*margin-bottom: 4px;", mobile);
        Assert.Matches(@"(?s)\.sidebar \.session-group-head,\s*\.sidebar \.session-group-more \.sl-show-more\s*\{[^}]*min-height: 32px;", mobile);
        Assert.Matches(@"(?s)\.sidebar \.session-row\s*\{[^}]*min-height: 36px;[^}]*padding: 4px;", mobile);
        Assert.Matches(@"(?s)\.sidebar \.session-list \.icon-btn\s*\{[^}]*height: 32px;[^}]*min-height: 32px;[^}]*min-width: 32px;[^}]*width: 32px;", mobile);
    }

    [Fact]
    public async Task ComposerPickers_ShareTheBottomToolbar()
    {
        var body = await _harness.Client.GetStringAsync(string.Empty);
        var toolbars = Regex.Matches(body, @"(?s)<div class=""composer-toolbar"">.*?<input class=""composer-file""");
        Assert.Equal(2, toolbars.Count);
        foreach (Match toolbar in toolbars)
        {
            Assert.Matches(@"(?s)class=""mode-picker"".*?class=""toolbar-spacer"".*?class=""composer-models"".*?class=""model-select"".*?class=""effort-select"".*?class=""composer-send send-btn""", toolbar.Value);
            Assert.Contains("class=\"picker-icon effort-icon\"", toolbar.Value);
            Assert.Contains("class=\"picker-label model-label\"", toolbar.Value);
            Assert.DoesNotContain(">Thinking</span>", toolbar.Value);
        }
    }

    [Fact]
    public async Task ComposerStyles_KeepPickersAtDesktopSizeAndBoundLongLabels()
    {
        var css = await _harness.Client.GetStringAsync("styles.css");
        Assert.Matches(@"(?s)\.model-picker\s*\{[^}]*max-width: 240px;[^}]*min-width: 40px;", css);
        Assert.Matches(@"(?s)\.effort-picker\s*\{[^}]*flex: 0 1 auto;[^}]*max-width: 112px;[^}]*min-width: 66px;", css);
        Assert.Matches(@"(?s)\.picker-label\s*\{[^}]*text-overflow: ellipsis;[^}]*white-space: nowrap;", css);
        Assert.Matches(@"(?s)\.composer-picker\s*\{[^}]*height: 28px;", css);
        Assert.Contains("@container (max-width: 560px)", css);
        var compact = css[css.IndexOf("@container (max-width: 560px)", StringComparison.Ordinal)..];
        Assert.Matches(@"(?s)\.effort-picker\s*\{[^}]*min-width: 74px;", compact);
        Assert.Matches(@"(?s)\.plan-badge\s*\{[^}]*padding: 0 2px;", compact);
        Assert.Matches(@"(?s)\.toolbar-spacer\s*\{[^}]*display: none;", compact);
    }

    [Fact]
    public async Task TranscriptStyles_KeepToolRowsAtDesktopDensity()
    {
        using var response = await _harness.Client.GetAsync("styles.css");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var css = await response.Content.ReadAsStringAsync();
        var header = Assert.Single(Regex.Matches(css, @"(?m)^\s*\.tool-head\s*\{([^}]*)\}").Cast<Match>()).Groups[1].Value;
        Assert.Contains("min-height: 28px;", header);
        Assert.Contains("line-height: 20px;", header);
        Assert.Contains("padding: 3px 0;", header);
        Assert.Contains("grid-template-columns: 16px minmax(0, 1fr) auto 16px;", header);
        foreach (var selector in new[] { "tool", "thinking" })
        {
            var rule = Assert.Single(Regex.Matches(css, $@"(?m)^\s*\.{selector}\s*\{{([^}}]*)\}}").Cast<Match>()).Groups[1].Value;
            Assert.Contains("margin: 0 0 4px;", rule);
        }
        var reasoning = Assert.Single(Regex.Matches(css, @"(?m)^\s*\.row-assistant:has\(> \.thinking:only-child\)\s*\{([^}]*)\}").Cast<Match>()).Groups[1].Value;
        Assert.Contains("margin: 0 0 20px;", Assert.Single(Regex.Matches(css, @"(?m)^\s*\.row-assistant\s*\{([^}]*)\}").Cast<Match>()).Groups[1].Value);
        Assert.Contains("margin-bottom: 4px;", reasoning);
        var label = Assert.Single(Regex.Matches(css, @"(?m)^\s*\.tool-label\s*\{([^}]*)\}").Cast<Match>()).Groups[1].Value;
        Assert.Contains("flex-wrap: nowrap;", label);
        var name = Assert.Single(Regex.Matches(css, @"(?m)^\s*\.tool-name\s*\{([^}]*)\}").Cast<Match>()).Groups[1].Value;
        Assert.Contains("max-width: 120px;", name);
        Assert.Contains("overflow: hidden;", name);
        Assert.Contains("text-overflow: ellipsis;", name);
        Assert.Contains("white-space: nowrap;", name);
        Assert.DoesNotContain("overflow-wrap: anywhere;", name);
        Assert.DoesNotMatch(@"(?s)\.tool-head\.is-compact \.tool-name\s*\{[^}]*flex: none;", css);
        Assert.Matches(@"(?s)\[hidden\]\s*\{[^}]*display: none !important;", css);
    }

    [Fact]
    public async Task ToolStyles_KeepSearchAndSubagentsAtDesktopPresentation()
    {
        var css = await _harness.Client.GetStringAsync("styles.css");
        Assert.Matches(@"(?s)\.tool-head\.is-search \.tool-summary,\s*\.tool-head\.is-agent \.tool-summary\s*\{[^}]*color: var\(--tertiary\);", css);
        Assert.Matches(@"(?s)\.tool-agent-type\s*\{[^}]*color: var\(--info\);[^}]*font-family: var\(--mono\);[^}]*font-size: 14px;[^}]*max-width: 144px;[^}]*text-overflow: ellipsis;[^}]*white-space: nowrap;", css);
        Assert.Matches(@"(?s)\.tool-agent-type\.is-explore\s*\{[^}]*color: var\(--agent-explore\);", css);
        Assert.Contains("--agent-explore: #77d6be;", css);
        Assert.Matches(@"(?s)\.tool-head\.is-agent\s*\{[^}]*grid-template-columns: 16px minmax\(0, 1fr\) auto;", css);
        Assert.Matches(@"(?s)\.tool-head\.is-agent\.is-running \.tool-name\s*\{[^}]*animation: agent-pulse 4s", css);
        Assert.Matches(@"(?s)\.tool-head\.is-search \.tool-chev\s*\{[^}]*opacity: 0;", css);
    }

    [Fact]
    public async Task WwwRoot_ServesJavaScript()
    {
        using var response = await _harness.Client.GetAsync("wwwroot/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "javascript",
            response.Content.Headers.ContentType?.MediaType ?? "",
            StringComparison.OrdinalIgnoreCase);
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrEmpty(body));
    }

    [Theory]
    [InlineData("wwwroot/session-list.js")]
    [InlineData("wwwroot/composer.js")]
    [InlineData("wwwroot/slash.js")]
    [InlineData("wwwroot/submission.js")]
    [InlineData("wwwroot/queue.js")]
    public async Task WwwRoot_ServesShellModules(string path)
    {
        using var response = await _harness.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "javascript",
            response.Content.Headers.ContentType?.MediaType ?? "",
            StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
    }

    [Theory]
    [InlineData("icons/favicon.svg")]
    [InlineData("wwwroot/icons/favicon.svg")]
    public async Task Branding_ServesOriginalBottleAndTerminalSvg(string path)
    {
        using var response = await _harness.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        XNamespace svg = "http://www.w3.org/2000/svg";
        var root = Assert.IsType<XElement>(document.Root);
        Assert.Equal("0 0 512 512", root.Attribute("viewBox")?.Value);
        Assert.Equal("Alcoler", root.Element(svg + "title")?.Value);
        Assert.Contains(root.Descendants(svg + "path"), element =>
            element.Attribute("d")?.Value.StartsWith("M228 48 H284 A14", StringComparison.Ordinal) == true);
        Assert.Contains(root.Descendants(svg + "polyline"), element =>
            element.Attribute("points")?.Value == "200,282 256,328 200,374");
        Assert.Contains(root.Descendants(svg + "rect"), element =>
            element.Attribute("fill")?.Value == "#35c2a1" && element.Attribute("width")?.Value == "44");
    }

    [Theory]
    [InlineData("icons/icon-192.png", 192)]
    [InlineData("icons/icon-512.png", 512)]
    [InlineData("icons/icon-maskable-192.png", 192)]
    [InlineData("icons/icon-maskable-512.png", 512)]
    [InlineData("icons/apple-touch-icon.png", 180)]
    public async Task Branding_ServesPngAtDeclaredDimensions(string path, int size)
    {
        using var response = await _harness.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 24);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.AsSpan(0, 8).ToArray());
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    [Fact]
    public async Task WwwRoot_ServesManifestAsManifestJson()
    {
        using var response = await _harness.Client.GetAsync("wwwroot/manifest.webmanifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var icons = manifest.RootElement.GetProperty("icons").EnumerateArray().ToArray();
        Assert.Equal(4, icons.Length);
        Assert.Contains(icons, icon => icon.GetProperty("src").GetString() == "/icons/icon-192.png" &&
            icon.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(icons, icon => icon.GetProperty("src").GetString() == "/icons/icon-512.png" &&
            icon.GetProperty("sizes").GetString() == "512x512");
        foreach (var size in new[] { 192, 512 })
            Assert.Contains(icons, icon => icon.GetProperty("src").GetString() == $"/icons/icon-maskable-{size}.png" &&
                icon.GetProperty("sizes").GetString() == $"{size}x{size}" &&
                icon.GetProperty("purpose").GetString() == "maskable");
    }
}
