using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alcoler.Adapters.Web;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class WebFetchTool : ITool, IDisposable
{
    private const int MaxUrlChars = 2_000;
    private const int MaxResponseBytes = 10 * 1024 * 1024;
    private const int MaxModelInputChars = 100_000;
    private const int AuxMaxOutputTokens = 4_096;
    private const int MaxRedirects = 10;

    private const string TruncationSuffix = "\n\n[WebFetch content truncated before prompt processing]";

    public const string DescriptionText =
        """
        Fetches a URL, converts the page to markdown, and answers `prompt` against it using a small fast model.

        - Fails on authenticated/private URLs — use an authenticated MCP tool or `gh` for those instead.
        - HTTP is upgraded to HTTPS. Cross-host redirects are returned to you rather than followed; call again with the redirect URL.
        - Responses are cached for 15 minutes per URL.
        """;

    private const string AuxInstruction =
        """
        Provide a concise response based only on the content above. In your response:
         - Enforce a strict 125-character maximum for quotes from any source document. Open Source Software is ok as long as we respect the license.
         - Use quotation marks for exact language from articles; any language outside of the quotation should never be word-for-word the same.
         - You are not a lawyer and never comment on the legality of your own prompts and responses.
         - Never produce or reproduce exact song lyrics.
        """;

    private static readonly Regex StatusWordBoundary = new("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.CultureInvariant);
    private static readonly Regex NumericRetryAfter = new("^[0-9]{1,6}$", RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly WebFetchCache _cache;

    public WebFetchTool(HttpMessageHandler? handler = null, WebFetchCache? cache = null)
    {
        _cache = cache ?? new WebFetchCache();
        _httpClient = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
            : new HttpClient(handler, disposeHandler: false);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Alcoler-WebFetch/0.1 (+coding-agent)");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/markdown, text/html, */*");
    }

    public ToolContract Contract { get; } = new(
        "WebFetch",
        DescriptionText,
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The URL to fetch content from" },
            "prompt": { "type": "string", "description": "The prompt to run on the fetched content" }
          },
          "required": ["url", "prompt"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: false,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.Network,
        RiskLevel: RiskLevel.Medium,
        NeedsApproval: true,
        AllowedInPlanMode: true,
        DefaultTimeoutMs: 60_000,
        MaxTimeoutMs: 60_000);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var rawUrl = ToolInput.String(context.Input, "url");
        var prompt = ToolInput.String(context.Input, "prompt");
        var now = context.Clock.GetUtcNow();

        var invalidUrl = ValidateUrl(rawUrl, out var requestUri);
        if (invalidUrl is not null)
            return ToolExecutionResult.Text(invalidUrl, isError: true);

        var fetched = _cache.TryGet(rawUrl, now);
        if (fetched is null)
        {
            var outcome = await FetchAsync(requestUri, prompt, cancellationToken).ConfigureAwait(false);
            if (outcome.Terminal is not null)
                return outcome.Terminal;
            fetched = outcome.Content!;
            _cache.Set(rawUrl, fetched, now);
        }

        return await SummarizeAsync(prompt, fetched, IsPreapprovedUrl(requestUri), context.AuxModel, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _httpClient.Dispose();

    private static string? ValidateUrl(string rawUrl, out Uri requestUri)
    {
        requestUri = null!;
        if (rawUrl.Length > MaxUrlChars)
            return "URL is too long";
        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var parsed))
            return $"Invalid URL: {rawUrl}";
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return "WebFetch only supports http and https URLs";
        if (parsed.UserInfo.Length > 0)
            return "WebFetch URLs must not include credentials";

        requestUri = parsed.Scheme == Uri.UriSchemeHttp ? UpgradeToHttps(parsed) : parsed;
        return GetBlockedHostReason(requestUri);
    }

    private static Uri UpgradeToHttps(Uri uri)
    {
        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps };
        if (uri.IsDefaultPort)
            builder.Port = -1;
        return builder.Uri;
    }

    private static string? GetBlockedHostReason(Uri uri)
    {
        var hostname = uri.Host;
        if (hostname.Length == 0)
            return "URL must include a hostname";

        if (IPAddress.TryParse(hostname, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return IsBlockedIPv4(address.GetAddressBytes()) ? "WebFetch blocks non-public IPv4 hosts" : null;
            return IsBlockedIPv6(address) ? "WebFetch blocks non-public IPv6 hosts" : null;
        }

        if (hostname is "localhost" || hostname.EndsWith(".localhost") || hostname.EndsWith(".local"))
            return "WebFetch requires a public hostname";

        if (hostname.Split('.').Length < 2)
            return "Invalid URL";

        return null;
    }

    // Covers the private, loopback, link-local, CGNAT, and reserved/multicast IPv4 space.
    private static bool IsBlockedIPv4(byte[] b) =>
        b[0] == 0
        || b[0] == 10
        || b[0] == 127
        || b[0] >= 224
        || (b[0] == 100 && (b[1] & 0xC0) == 0x40)
        || (b[0] == 169 && b[1] == 254)
        || (b[0] == 172 && b[1] is >= 16 and <= 31)
        || (b[0] == 192 && b[1] == 168);

    private static bool IsBlockedIPv6(IPAddress address) =>
        IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;

    private async ValueTask<FetchOutcome> FetchAsync(Uri requestUri, string prompt, CancellationToken cancellationToken)
    {
        var current = requestUri;
        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient
                    .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FetchOutcome.FromTerminal(ToolExecutionResult.Text($"WebFetch request failed: {ex.Message}", isError: true));
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode))
                {
                    var outcome = ClassifyRedirect(response, current, prompt);
                    if (outcome.NextUri is null)
                        return outcome;
                    current = outcome.NextUri;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return FetchOutcome.FromTerminal(HttpErrorResult(response, current));

                string body;
                try
                {
                    body = await ReadBodyCappedAsync(response, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    return FetchOutcome.FromTerminal(ToolExecutionResult.Text(ex.Message, isError: true));
                }

                var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
                string content;
                try
                {
                    content = HtmlToMarkdownConverter.ExtractReadableContent(body, contentType);
                }
                catch (InvalidOperationException ex)
                {
                    return FetchOutcome.FromTerminal(ToolExecutionResult.Text(ex.Message, isError: true));
                }

                return new FetchOutcome(new WebFetchContent(content, contentType));
            }
        }

        return FetchOutcome.FromTerminal(ToolExecutionResult.Text("WebFetch exceeded the safe redirect limit", isError: true));
    }

    private static FetchOutcome ClassifyRedirect(HttpResponseMessage response, Uri current, string prompt)
    {
        var location = response.Headers.Location;
        if (location is null || string.IsNullOrWhiteSpace(location.OriginalString))
            return FetchOutcome.FromTerminal(HttpErrorResult(response, current));

        Uri next;
        try
        {
            next = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        catch (UriFormatException)
        {
            return FetchOutcome.FromTerminal(ToolExecutionResult.Text(
                $"Redirect Location is not a valid URL: {location.OriginalString}",
                isError: true));
        }

        if (!IsPermittedRedirect(current, next))
            return FetchOutcome.FromTerminal(RedirectResult(current, RedactCredentials(next), prompt, (int)response.StatusCode, response.ReasonPhrase));
        return new FetchOutcome(NextUri: next);
    }

    private static bool IsPermittedRedirect(Uri from, Uri to) =>
        to.UserInfo.Length == 0
        && GetBlockedHostReason(to) is null
        && from.Scheme == to.Scheme
        && from.Port == to.Port
        && StripWww(from.Host) == StripWww(to.Host);

    private static ToolExecutionResult RedirectResult(Uri original, Uri redirectUrl, string prompt, int status, string? reasonPhrase) =>
        ToolExecutionResult.Text(string.Join("\n",
            "REDIRECT DETECTED: The URL redirects to a different host.",
            "",
            $"Original URL: {original}",
            $"Redirect URL: {redirectUrl}",
            $"Status: {status} {StatusText(status, reasonPhrase)}",
            "",
            "To complete your request, I need to fetch content from the redirected URL. Please use WebFetch again with these parameters:",
            $"- url: \"{redirectUrl}\"",
            $"- prompt: \"{prompt}\""));

    private static ToolExecutionResult HttpErrorResult(HttpResponseMessage response, Uri current)
    {
        var status = (int)response.StatusCode;
        var retryAfter = response.Headers.TryGetValues("Retry-After", out var values)
            ? values.FirstOrDefault() ?? ""
            : "";
        var retrySuffix = NumericRetryAfter.IsMatch(retryAfter) ? $"\nRetry-After: {retryAfter}" : "";
        return ToolExecutionResult.Text(
            $"The server returned HTTP {status} {StatusText(status, response.ReasonPhrase)}.{retrySuffix}\n\n" +
            "The response body was not retrieved. If this URL requires authentication, use an authenticated tool (e.g. `gh` for GitHub, or an MCP-provided fetch tool) instead of WebFetch.",
            isError: true);
    }

    private static async ValueTask<string> ReadBodyCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var writer = new ArrayBufferWriter<byte>(81920);
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, 81920), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (writer.WrittenCount + read > MaxResponseBytes)
                    throw new InvalidOperationException($"WebFetch response exceeded the maximum of {MaxResponseBytes} bytes.");
                chunk.AsSpan(0, read).CopyTo(writer.GetSpan(read));
                writer.Advance(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    private static async ValueTask<ToolExecutionResult> SummarizeAsync(
        string prompt,
        WebFetchContent fetched,
        bool preapproved,
        IAuxModelClient? auxModel,
        CancellationToken cancellationToken)
    {
        if (preapproved && fetched.ContentType.Contains("text/markdown", StringComparison.OrdinalIgnoreCase)
            && fetched.Content.Length < MaxModelInputChars)
            return ToolExecutionResult.Text(fetched.Content);
        if (auxModel is null)
            throw new InvalidOperationException("Model is not configured for WebFetch prompt processing");

        var content = fetched.Content;
        if (content.Length > MaxModelInputChars)
            content = content[..(MaxModelInputChars - TruncationSuffix.Length)] + TruncationSuffix;

        var instruction = preapproved
            ? "Provide a concise response based on the content above. Include relevant details, code examples, and documentation excerpts as needed."
            : AuxInstruction;
        string answer;
        try
        {
            answer = (await auxModel.CompleteAsync(
                new AuxModelRequest(
                    "",
                    $"\nWeb page content:\n---\n{content}\n---\n\n{prompt}\n\n{instruction}\n",
                    AuxMaxOutputTokens),
                cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Text(ex.Message, isError: true);
        }

        return ToolExecutionResult.Text(
            answer.Length > 0 ? answer : "WebFetch completed, but the extraction model returned no text.");
    }

    private static readonly HashSet<string> PreapprovedHosts = new(StringComparer.Ordinal)
    {
        "modelcontextprotocol.io", "agentskills.io", "docs.python.org", "en.cppreference.com", "docs.oracle.com",
        "learn.microsoft.com", "developer.mozilla.org", "go.dev", "pkg.go.dev", "www.php.net", "docs.swift.org",
        "kotlinlang.org", "ruby-doc.org", "doc.rust-lang.org", "www.typescriptlang.org", "react.dev", "angular.io",
        "vuejs.org", "nextjs.org", "expressjs.com", "nodejs.org", "bun.sh", "jquery.com", "getbootstrap.com",
        "tailwindcss.com", "d3js.org", "threejs.org", "redux.js.org", "webpack.js.org", "jestjs.io", "reactrouter.com",
        "docs.djangoproject.com", "flask.palletsprojects.com", "fastapi.tiangolo.com", "pandas.pydata.org", "numpy.org",
        "www.tensorflow.org", "pytorch.org", "scikit-learn.org", "matplotlib.org", "requests.readthedocs.io", "jupyter.org",
        "laravel.com", "symfony.com", "docs.spring.io", "hibernate.org", "tomcat.apache.org", "gradle.org", "maven.apache.org",
        "asp.net", "dotnet.microsoft.com", "blazor.net", "reactnative.dev", "docs.flutter.dev", "developer.apple.com",
        "developer.android.com", "keras.io", "spark.apache.org", "www.mongodb.com", "redis.io", "www.postgresql.org",
        "dev.mysql.com", "www.sqlite.org", "graphql.org", "prisma.io", "docs.getdbt.com", "docs.aws.amazon.com",
        "cloud.google.com", "kubernetes.io", "www.docker.com", "www.terraform.io", "www.ansible.com", "docs.stripe.com",
        "docs.netlify.com", "devcenter.heroku.com", "cypress.io", "selenium.dev", "docs.unity.com", "docs.unrealengine.com",
        "git-scm.com", "nginx.org", "httpd.apache.org",
    };

    internal static bool IsPreapprovedUrl(Uri url)
    {
        if (PreapprovedHosts.Contains(url.Host))
            return true;
        var prefix = url.Host switch
        {
            "wordpress.org" => "/documentation",
            "huggingface.co" or "www.kaggle.com" or "vercel.com" => "/docs",
            _ => null,
        };
        if (prefix is null || Regex.IsMatch(url.AbsolutePath, @"%(25)*(2f|5c|2e)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        return url.AbsolutePath == prefix || url.AbsolutePath.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or (HttpStatusCode)307
            or (HttpStatusCode)308;

    private static string StatusText(int status, string? reasonPhrase)
    {
        var trimmed = reasonPhrase?.Trim();
        if (trimmed is { Length: > 0 })
            return trimmed;
        if (Enum.IsDefined(typeof(HttpStatusCode), (HttpStatusCode)status))
            return StatusWordBoundary.Replace(((HttpStatusCode)status).ToString(), " ");
        return "Unknown Status";
    }

    private static Uri RedactCredentials(Uri uri) =>
        uri.UserInfo.Length == 0 ? uri : new UriBuilder(uri) { UserName = "", Password = "" }.Uri;

    private static string StripWww(string hostname) =>
        hostname.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? hostname[4..] : hostname;

    private sealed record FetchOutcome(WebFetchContent? Content = null, ToolExecutionResult? Terminal = null, Uri? NextUri = null)
    {
        public static FetchOutcome FromTerminal(ToolExecutionResult result) => new(Terminal: result);
    }
}
