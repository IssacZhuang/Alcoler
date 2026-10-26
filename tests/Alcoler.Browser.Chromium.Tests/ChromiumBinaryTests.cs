using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Alcoler.Browser.Chromium;
using Xunit;

namespace Alcoler.Browser.Chromium.Tests;

// One class on purpose: ALCOLER_BROWSER_EXECUTABLE is process-global and xunit runs tests within a class sequentially.
public sealed class ChromiumBinaryTests
{
    [Fact]
    public async Task ExplicitPathBeatsEnvironmentAndInstalled()
    {
        var explicitPath = CreateFakeExecutable("explicit-chrome");
        var environmentPath = CreateFakeExecutable("environment-chrome");
        var installedPath = CreateFakeExecutable("installed-chrome");
        Environment.SetEnvironmentVariable(ChromiumBinary.ExecutableEnvironmentVariable, environmentPath);
        try
        {
            var binary = NewBinary(allowDownload: false, candidateProvider: () => [installedPath]);
            Assert.Equal(Path.GetFullPath(explicitPath), await binary.ResolveAsync(explicitPath, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChromiumBinary.ExecutableEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task EnvironmentBeatsInstalled()
    {
        var environmentPath = CreateFakeExecutable("environment-only");
        var installedPath = CreateFakeExecutable("installed-only");
        Environment.SetEnvironmentVariable(ChromiumBinary.ExecutableEnvironmentVariable, environmentPath);
        try
        {
            var binary = NewBinary(allowDownload: false, candidateProvider: () => [installedPath]);
            Assert.Equal(Path.GetFullPath(environmentPath), await binary.ResolveAsync(null, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChromiumBinary.ExecutableEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task InstalledCandidateUsedWhenNoExplicitOrEnvironment()
    {
        var installedPath = CreateFakeExecutable("first-installed");
        var missing = Path.Combine(Path.GetTempPath(), "alcoler-missing-chrome-" + Guid.NewGuid().ToString("N"));
        var binary = NewBinary(allowDownload: false, candidateProvider: () => [missing, installedPath]);
        Assert.Equal(installedPath, await binary.ResolveAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task MissingExplicitPathThrowsWithTheConfiguredPath()
    {
        var binary = NewBinary(allowDownload: false, candidateProvider: () => []);
        var configured = Path.Combine(Path.GetTempPath(), "alcoler-absent-" + Guid.NewGuid().ToString("N"));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => binary.ResolveAsync(configured, CancellationToken.None));
        Assert.Contains(configured, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingFoundWithoutDownloadThrowsActionably()
    {
        var binary = NewBinary(allowDownload: false, candidateProvider: () => []);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => binary.ResolveAsync(null, CancellationToken.None));
        Assert.Contains(ChromiumBinary.ExecutableEnvironmentVariable, failure.Message, StringComparison.Ordinal);
        Assert.Contains("browser.executablePath", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedDownloadsOnceThenStampsTheCache()
    {
        var cache = Directory.CreateTempSubdirectory("alcoler-chromium-binary-").FullName;
        try
        {
            var version = "131.6778.0";
            var fetches = 0;
            var downloads = 0;
            var handler = new FakeHandler(request =>
            {
                if (request.RequestUri!.AbsoluteUri.EndsWith("versions.json", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref fetches);
                    return Json(VersionsJson(version));
                }
                Interlocked.Increment(ref downloads);
                return Zip(ConventionalEntry());
            });
            var binary = new ChromiumBinary(cache, new HttpClient(handler), candidateProvider: () => []);
            var resolved = await binary.ResolveAsync(null, CancellationToken.None);
            Assert.True(File.Exists(resolved));
            Assert.Equal(ChromiumBinary.ManagedExecutableFileName(), Path.GetFileName(resolved));
            Assert.StartsWith(cache, resolved, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(cache, "chrome-" + version, ".alcoler-chrome-stamp")));
            Assert.Equal(1, fetches);
            Assert.Equal(1, downloads);

            var offline = new ChromiumBinary(cache, new HttpClient(new FakeHandler(_ => Json("{}"))), candidateProvider: () => []);
            Assert.Equal(resolved, await offline.ResolveAsync(null, CancellationToken.None));
            Assert.Equal(1, fetches);
            Assert.Equal(1, downloads);
        }
        finally
        {
            TryDeleteDirectory(cache);
        }
    }

    [Fact]
    public async Task ManagedDownloadLocatesTheBinaryWhenArchiveLayoutDiffers()
    {
        var cache = Directory.CreateTempSubdirectory("alcoler-chromium-binary-").FullName;
        try
        {
            var unusual = "nested/directory/" + ChromiumBinary.ManagedExecutableFileName();
            var handler = new FakeHandler(request =>
                request.RequestUri!.AbsoluteUri.EndsWith("versions.json", StringComparison.Ordinal)
                    ? Json(VersionsJson("130.0.0.1"))
                    : Zip(unusual));
            var binary = new ChromiumBinary(cache, new HttpClient(handler), candidateProvider: () => []);
            var resolved = await binary.ResolveAsync(null, CancellationToken.None);
            Assert.Equal(ChromiumBinary.ManagedExecutableFileName(), Path.GetFileName(resolved));
        }
        finally
        {
            TryDeleteDirectory(cache);
        }
    }

    [Fact]
    public async Task FailingManagedDownloadThrowsActionably()
    {
        var cache = Directory.CreateTempSubdirectory("alcoler-chromium-binary-").FullName;
        try
        {
            var handler = new FakeHandler(request =>
                request.RequestUri!.AbsoluteUri.EndsWith("versions.json", StringComparison.Ordinal)
                    ? Json(VersionsJson("129.0.0.1"))
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var binary = new ChromiumBinary(cache, new HttpClient(handler), candidateProvider: () => []);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => binary.ResolveAsync(null, CancellationToken.None));
            Assert.Contains(ChromiumBinary.ExecutableEnvironmentVariable, failure.Message, StringComparison.Ordinal);
            Assert.Contains("The managed download failed", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(cache);
        }
    }

    private static ChromiumBinary NewBinary(bool allowDownload, Func<IEnumerable<string>> candidateProvider) =>
        new(Path.Combine(Path.GetTempPath(), "alcoler-chromium-cache-" + Guid.NewGuid().ToString("N")),
            candidateProvider: candidateProvider, allowDownload: allowDownload);

    private static string CreateFakeExecutable(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string VersionsJson(string version)
    {
        var platform = ChromiumBinary.ChromeForTestingPlatform();
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["channels"] = new Dictionary<string, object?>
            {
                ["Stable"] = new Dictionary<string, object?>
                {
                    ["version"] = version,
                    ["downloads"] = new Dictionary<string, object?>
                    {
                        ["chrome"] = new object[]
                        {
                            new Dictionary<string, object?> { ["platform"] = platform, ["url"] = "https://storage.example/chrome.zip" },
                        },
                    },
                },
            },
        });
    }

    private static string ConventionalEntry() => ChromiumBinary.ChromeForTestingPlatform() switch
    {
        "win64" => "chrome-win64/chrome.exe",
        "linux64" => "chrome-linux64/chrome",
        "mac-arm64" => "chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing",
        _ => "chrome-mac-x64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing",
    };

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content),
    };

    private static HttpResponseMessage Zip(params string[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                using var stream = archive.CreateEntry(entry).Open();
                stream.Write("binary"u8);
            }
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
