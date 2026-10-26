using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Alcoler.Browser.Chromium;

public sealed class ChromiumBinary
{
    public const string DefaultVersionsUrl = "https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions.json";
    public const string ExecutableEnvironmentVariable = "ALCOLER_BROWSER_EXECUTABLE";

    // Managed downloads must be process-wide serialized: the stamp check happens before the lock, so the
    // second waiter must recheck for a completed install instead of downloading again.
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    private readonly string _cacheRoot;
    private readonly HttpClient? _httpClient;
    private readonly string _versionsUrl;
    private readonly Func<IEnumerable<string>>? _candidateProvider;
    private readonly bool _allowDownload;

    public ChromiumBinary(string? cacheRoot = null, HttpClient? httpClient = null, string? versionsUrl = null,
        Func<IEnumerable<string>>? candidateProvider = null, bool allowDownload = true)
    {
        _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alcoler", "browser-runtime")
            : Path.GetFullPath(cacheRoot);
        _httpClient = httpClient;
        _versionsUrl = versionsUrl ?? DefaultVersionsUrl;
        _candidateProvider = candidateProvider;
        _allowDownload = allowDownload;
    }

    public static string ChromeForTestingPlatform()
    {
        if (OperatingSystem.IsWindows()) return "win64";
        if (OperatingSystem.IsLinux()) return "linux64";
        return RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "mac-x64" : "mac-arm64";
    }

    public static string ManagedExecutableFileName() => ChromeForTestingPlatform() switch
    {
        "win64" => "chrome.exe",
        "linux64" => "chrome",
        _ => "Google Chrome for Testing",
    };

    public static IEnumerable<string> InstalledCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("LocalAppData"),
            };
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(root, "Google", "Chrome Beta", "Application", "chrome.exe");
                yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            }
            foreach (var candidate in SearchPath(["chrome.exe", "msedge.exe"])) yield return candidate;
            yield break;
        }
        if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/google-chrome-stable";
            yield return "/usr/bin/google-chrome";
            yield return "/usr/bin/chromium";
            yield return "/usr/bin/chromium-browser";
            yield return "/usr/bin/microsoft-edge";
            yield return "/snap/bin/chromium";
            foreach (var candidate in SearchPath(
                ["google-chrome-stable", "google-chrome", "chromium", "chromium-browser", "microsoft-edge"]))
            {
                yield return candidate;
            }
            yield break;
        }
        yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
        yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        foreach (var candidate in SearchPath(["Google Chrome", "Chromium", "Microsoft Edge"]))
        {
            yield return candidate;
        }
    }

    public static string? FindInstalled(Func<IEnumerable<string>>? candidateProvider = null)
    {
        foreach (var candidate in (candidateProvider ?? InstalledCandidates)())
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task<string> ResolveAsync(string? explicitPath, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath))
            {
                throw new InvalidOperationException(
                    $"The configured browser executable '{explicitPath}' does not exist. Fix browser.executablePath or clear it to use automatic detection.");
            }
            return Path.GetFullPath(explicitPath);
        }
        var fromEnvironment = Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment)) return Path.GetFullPath(fromEnvironment);

        var installed = FindInstalled(_candidateProvider);
        if (installed is not null) return installed;

        Exception? downloadFailure = null;
        try
        {
            var managed = await ResolveManagedAsync(cancellationToken).ConfigureAwait(false);
            if (managed is not null) return managed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            downloadFailure = ex;
        }
        var message = "No Chromium-family browser was found. Install Google Chrome, Chromium, or Microsoft Edge, " +
            $"set browser.executablePath or the {ExecutableEnvironmentVariable} environment variable, " +
            "or enable the managed Chrome-for-Testing download.";
        if (downloadFailure is not null) message += $" The managed download failed: {downloadFailure.Message}";
        throw new InvalidOperationException(message);
    }

    private async Task<string?> ResolveManagedAsync(CancellationToken cancellationToken)
    {
        if (!_allowDownload || _httpClient is null) return null;
        var existing = FindExistingManaged();
        if (existing is not null) return existing;
        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = FindExistingManaged();
            if (existing is not null) return existing;
            var (version, url) = await FetchStableAsync(cancellationToken).ConfigureAwait(false);
            var root = Path.Combine(_cacheRoot, "chrome-" + version);
            if (File.Exists(Path.Combine(root, StampName)))
            {
                var stamped = LocateExecutable(root);
                if (stamped is not null) return stamped;
            }
            await DownloadAndExtractAsync(url, root, cancellationToken).ConfigureAwait(false);
            return LocateExecutable(root) ?? throw new InvalidOperationException(
                $"The managed Chrome-for-Testing archive for {version} did not contain a browser executable.");
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private string? FindExistingManaged()
    {
        if (!Directory.Exists(_cacheRoot)) return null;
        foreach (var directory in Directory.EnumerateDirectories(_cacheRoot, "chrome-*"))
        {
            if (!File.Exists(Path.Combine(directory, StampName))) continue;
            var executable = LocateExecutable(directory);
            if (executable is not null) return executable;
        }
        return null;
    }

    private async Task<(string Version, string Url)> FetchStableAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient!.GetAsync(_versionsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var stable = document.RootElement.GetProperty("channels").GetProperty("Stable");
        var version = stable.GetProperty("version").GetString()
            ?? throw new InvalidOperationException("The Chrome-for-Testing versions feed omitted the stable version.");
        var platform = ChromeForTestingPlatform();
        foreach (var entry in stable.GetProperty("downloads").GetProperty("chrome").EnumerateArray())
        {
            if (entry.TryGetProperty("platform", out var name) && name.GetString() == platform
                && entry.TryGetProperty("url", out var link) && link.GetString() is { Length: > 0 } url)
            {
                return (version, url);
            }
        }
        throw new InvalidOperationException($"The Chrome-for-Testing versions feed has no Stable chrome download for platform '{platform}'.");
    }

    private async Task DownloadAndExtractAsync(string url, string destinationRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheRoot);
        var zipPath = Path.Combine(_cacheRoot, Path.GetRandomFileName());
        var staging = Path.Combine(_cacheRoot, ".staging-" + Path.GetRandomFileName());
        try
        {
            using (var file = File.Create(zipPath))
            {
                using var response = await _httpClient!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
            ZipFile.ExtractToDirectory(zipPath, staging);
            if (Directory.Exists(destinationRoot)) Directory.Delete(destinationRoot, recursive: true);
            Directory.Move(staging, destinationRoot);
            await File.WriteAllTextAsync(Path.Combine(destinationRoot, StampName), string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            File.Delete(zipPath);
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? LocateExecutable(string root)
    {
        var convention = ChromeForTestingPlatform() switch
        {
            "win64" => Path.Combine("chrome-win64", "chrome.exe"),
            "linux64" => Path.Combine("chrome-linux64", "chrome"),
            "mac-arm64" => Path.Combine("chrome-mac-arm64", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"),
            _ => Path.Combine("chrome-mac-x64", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"),
        };
        var direct = Path.Combine(root, convention);
        if (File.Exists(direct)) return direct;
        var fileName = ManagedExecutableFileName();
        return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static IEnumerable<string> SearchPath(string[] names)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) yield break;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                yield return Path.Combine(directory, name);
            }
        }
    }

    private static string StampName => ".alcoler-chrome-stamp";
}
