using Alcoler.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alcoler.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "alcoler-log-" + Guid.NewGuid().ToString("N"));

    public FileLoggerProviderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void WritesMessagesWithCategoryAndException()
    {
        using var provider = new FileLoggerProvider(_directory, LogLevel.Information);
        var logger = provider.CreateLogger("Test.Category");

        logger.LogInformation("hello {Name}", "world");
        logger.LogError(new InvalidOperationException("boom"), "failed");

        provider.Flush(TimeSpan.FromSeconds(2));
        var file = Assert.Single(Directory.GetFiles(_directory, "alcoler-*.log"));
        var text = File.ReadAllText(file);
        Assert.Contains("Test.Category: hello world", text);
        Assert.Contains("[ERR]", text);
        Assert.Contains("InvalidOperationException", text);
    }

    [Fact]
    public void DropsEntriesBelowMinimumLevel()
    {
        using var provider = new FileLoggerProvider(_directory, LogLevel.Warning);
        var logger = provider.CreateLogger("Test.Category");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        logger.LogInformation("ignored");

        provider.Flush(TimeSpan.FromSeconds(2));
        Assert.Empty(Directory.GetFiles(_directory, "alcoler-*.log"));
    }

    [Fact]
    public void RotatesWhenFileReachesSizeLimit()
    {
        using var provider = new FileLoggerProvider(
            _directory, LogLevel.Information, maxFileBytes: 1024, maxTotalBytes: 1024 * 1024);
        var logger = provider.CreateLogger("Test.Category");

        for (var index = 0; index < 40; index++)
        {
            logger.LogInformation("entry {Index} {Padding}", index, new string('x', 80));
        }

        provider.Flush(TimeSpan.FromSeconds(2));
        Assert.True(Directory.GetFiles(_directory, "alcoler-*.log").Length > 1);
    }

    [Fact]
    public void PrunesFilesOlderThanRetention()
    {
        var expired = Path.Combine(_directory, "alcoler-20200101.log");
        File.WriteAllText(expired, "old");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-30));

        using var provider = new FileLoggerProvider(_directory, LogLevel.Information, retention: TimeSpan.FromDays(7));

        Assert.False(File.Exists(expired));
    }
}
