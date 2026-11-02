using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcoler.Logging;

public static class AppLog
{
    private static readonly object Gate = new();
    private static FileLoggerProvider? _provider;
    private static ILoggerFactory? _factory;
    private static ILogger _unhandledLogger = NullLogger.Instance;

    public static string DirectoryPath => WorkbenchDataPaths.LogsPath;

    public static ILoggerFactory Factory
    {
        get
        {
            lock (Gate)
            {
                return _factory ?? NullLoggerFactory.Instance;
            }
        }
    }

    public static ILogger CreateLogger(string category) => Factory.CreateLogger(category);

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_factory is not null)
            {
                return;
            }

            try
            {
                var minimumLevel = ResolveMinimumLevel();
                _provider = new FileLoggerProvider(DirectoryPath, minimumLevel);
                _factory = LoggerFactory.Create(builder =>
                {
                    builder.SetMinimumLevel(minimumLevel);
                    builder.AddProvider(_provider);
                });
                _unhandledLogger = _factory.CreateLogger("Alcoler.Unhandled");
                _factory.CreateLogger("Alcoler.Startup").LogInformation(
                    "Alcoler started. Log directory: {LogDirectory}", DirectoryPath);
            }
            catch (Exception)
            {
                _provider?.Dispose();
                _provider = null;
                _factory = NullLoggerFactory.Instance;
                _unhandledLogger = NullLogger.Instance;
            }
        }
    }

    public static void LogUnhandled(string source, Exception exception)
    {
        try
        {
            Volatile.Read(ref _unhandledLogger).LogCritical(exception, "Unhandled exception from {Source}.", source);
        }
        catch (Exception)
        {
        }
    }

    public static void Flush(TimeSpan timeout) => _provider?.Flush(timeout);

    public static void Shutdown()
    {
        lock (Gate)
        {
            var factory = _factory;
            _factory = null;
            _provider = null;
            _unhandledLogger = NullLogger.Instance;
            try
            {
                factory?.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    private static LogLevel ResolveMinimumLevel()
    {
        var configured = Environment.GetEnvironmentVariable("ALCOLER_LOG_LEVEL");
        return Enum.TryParse<LogLevel>(configured, ignoreCase: true, out var level) && level != LogLevel.None
            ? level
            : LogLevel.Information;
    }
}
