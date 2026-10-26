using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Alcoler.Browser.Chromium;
using Alcoler.Browser.Chromium.IntegrationTests;
using Alcoler.Browser.IntegrationTests;

namespace Alcoler.Browser.Chromium.IntegrationTests;

internal static class Program
{
    private static string? _outputDirectory;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        _outputDirectory = Environment.GetEnvironmentVariable("ALCOLER_CHROMIUM_SUITE_OUTPUT");
        var original = Console.Out;
        StreamWriter? log = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_outputDirectory))
            {
                Directory.CreateDirectory(_outputDirectory);
                log = new StreamWriter(Path.Combine(_outputDirectory, "run.log"), append: false) { AutoFlush = true };
                Console.SetOut(new TeeWriter(original, log));
            }
            return await RunAsync();
        }
        finally
        {
            Console.SetOut(original);
            log?.Dispose();
        }
    }

    private static async Task<int> RunAsync()
    {
        var headless = Environment.GetEnvironmentVariable("ALCOLER_CHROMIUM_SUITE_HEADLESS") == "1";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var root = Path.Combine(Path.GetTempPath(), "alcoler-chromium-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine($"Chromium integration suite: mode={(headless ? "headless" : "headed")}, dataRoot={root}");
        var clock = Stopwatch.StartNew();
        var results = new List<ScenarioResult>();
        try
        {
            string executable;
            try
            {
                executable = await new ChromiumBinary().ResolveAsync(null, deadline.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL browser executable resolution{Environment.NewLine}{ex.Message}");
                return 1;
            }
            var version = FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "<unknown>";
            Console.WriteLine($"Browser executable: {executable} (version {version})");
            await using var fixture = new LoopbackFixture();
            using var suite = new ChromiumSuite(root, fixture, headless, deadline.Token);
            foreach (var scenario in suite.Scenarios())
            {
                if (scenario.RequiresWindow && headless)
                {
                    Console.WriteLine($"SKIP {scenario.Name} (requires a real browser window; headless run)");
                    results.Add(new(scenario.Name, "skip", null));
                    continue;
                }
                Console.WriteLine($"RUN  {scenario.Name}");
                try
                {
                    await scenario.Body();
                    fixture.AssertHealthy();
                    results.Add(new(scenario.Name, "pass", null));
                    Console.WriteLine($"PASS {scenario.Name}");
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    results.Add(new(scenario.Name, "fail", "overall deadline exceeded"));
                    Console.WriteLine("FAIL overall deadline exceeded");
                    break;
                }
                catch (Exception ex)
                {
                    results.Add(new(scenario.Name, "fail", ex.ToString()));
                    Console.WriteLine($"FAIL {scenario.Name}{Environment.NewLine}{ex}");
                }
            }
            WriteResultsJson(results);
            var passed = results.Count(result => result.Outcome == "pass");
            var failed = results.Count(result => result.Outcome == "fail");
            Console.WriteLine($"{passed} pass / {failed} fail ({clock.Elapsed.TotalSeconds:F1}s)");
            return failed == 0 && passed > 0 ? 0 : 1;
        }
        finally
        {
            await DeleteRootAsync(root);
        }
    }

    private static void WriteResultsJson(List<ScenarioResult> results)
    {
        if (string.IsNullOrWhiteSpace(_outputDirectory)) return;
        try
        {
            File.WriteAllText(Path.Combine(_outputDirectory, "results.json"),
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN results.json could not be written: {ex.Message}");
        }
    }

    private static async Task DeleteRootAsync(string root)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                Console.WriteLine("CLEAN Temporary suite data root deleted.");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(500);
            }
        }
        Console.WriteLine($"CLEAN Temporary data root retained after retries: {root}{Environment.NewLine}{last}");
    }

    private sealed record ScenarioResult(string Name, string Outcome, string? Error);

    private sealed class TeeWriter(TextWriter console, TextWriter log) : TextWriter
    {
        public override Encoding Encoding => console.Encoding;

        public override void Write(char value)
        {
            console.Write(value);
            log.Write(value);
        }

        public override void Write(string? value)
        {
            console.Write(value);
            log.Write(value);
        }

        public override void WriteLine(string? value)
        {
            console.WriteLine(value);
            log.WriteLine(value);
        }
    }
}
