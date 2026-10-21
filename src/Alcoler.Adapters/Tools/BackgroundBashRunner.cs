using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Alcoler.Adapters.Tools;

internal sealed class BashProcessSession : IDisposable
{
    internal const string CwdMarker = "__ALCOLER_CWD__";
    internal const long MaxPersistedBytes = 5L * 1024 * 1024 * 1024;

    private readonly Process _process;
    private readonly object _sync = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _tail = new();
    private readonly bool _captureCwd;
    private readonly long _persistedLimit;
    private readonly Func<string, Stream> _openOutput;
    private Stream? _outputFile;
    private long _persistedBytes;
    private int _tailBytes;
    private Task _stdoutPump = Task.CompletedTask;
    private Task _stderrPump = Task.CompletedTask;
    private string? _capturedCwd;
    private string? _cwdCaptureScript;
    private bool _owned = true;
    private bool _limitExceeded;

    private BashProcessSession(Process process, bool captureCwd, int inlineLimit, long persistedLimit, Func<string, Stream>? openOutput)
    {
        _process = process;
        _captureCwd = captureCwd;
        InlineLimit = inlineLimit;
        _persistedLimit = persistedLimit;
        _openOutput = openOutput ?? (path => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096));
    }

    public string? OutputPath { get; private set; }
    public bool PersistenceFailed { get; private set; }
    public long OutputBytes { get; private set; }
    public int InlineLimit { get; }

    public static BashProcessSession Start(
        ShellSpec shell,
        string command,
        string workingDirectory,
        bool captureCwd,
        string? outputFilePath,
        long persistedLimit = MaxPersistedBytes,
        Func<string, Stream>? openOutput = null,
        Encoding? legacyEncoding = null,
        bool? isPosix = null)
    {
        var startInfo = new ProcessStartInfo(shell.FileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var posix = isPosix ?? IsPosixShell(shell);
        legacyEncoding ??= BashOutputDecoder.ResolveWindowsLegacyEncoding();
        var session = new BashProcessSession(new Process { StartInfo = startInfo }, captureCwd,
            ResolveInlineLimit(Environment.GetEnvironmentVariable("BASH_MAX_OUTPUT_LENGTH")), persistedLimit, openOutput);
        try
        {
            if (posix)
            {
                startInfo.ArgumentList.Add(shell.Arguments);
                startInfo.ArgumentList.Add("exec 2>&1; " + (captureCwd ? WrapWithCwdCapture(command) : command));
            }
            else
            {
                var script = command;
                if (captureCwd)
                {
                    session._cwdCaptureScript = Path.Combine(Path.GetTempPath(), $"alcoler-cwd-{Guid.NewGuid():N}.cmd");
                    File.WriteAllText(session._cwdCaptureScript,
                        $"@set \"__alcoler_rc=%errorlevel%\"\r\n@setlocal DisableDelayedExpansion\r\n@<nul set /p \"={CwdMarker}%CD%\"\r\n@exit /b %__alcoler_rc%\r\n",
                        Encoding.Default);
                    script = $"({command}) & \"{session._cwdCaptureScript}\"";
                }
                // CMD parses the raw /c tail; ArgumentList applies CRT quoting that changes embedded quotes.
                startInfo.Arguments = $"/d /s {shell.Arguments} \"{script}\"";
            }
            if (outputFilePath is not null)
                session.OpenOutputFile(outputFilePath);
            session._process.Start();
            session._stdoutPump = session.PumpAsync(session._process.StandardOutput.BaseStream, stdout: true, legacyEncoding);
            session._stderrPump = session.PumpAsync(session._process.StandardError.BaseStream, stdout: false, legacyEncoding);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal static bool IsPosixShell(ShellSpec shell) =>
        !string.Equals(Path.GetFileNameWithoutExtension(shell.FileName), "cmd", StringComparison.OrdinalIgnoreCase)
        && shell.Arguments == "-c";

    internal static int ResolveInlineLimit(string? value)
    {
        if (value is null)
            return BashModelContent.MaxInlineOutputBytes;
        var parsed = BashTimeoutPolicy.ParsePositive(value);
        return parsed is null ? BashModelContent.MaxInlineOutputBytes : Math.Min(parsed.Value, 150_000);
    }

    private static string WrapWithCwdCapture(string command) =>
        $$"""{ {{command}} ; } ; __rc=$?; printf '{{CwdMarker}}%s' "$(cygpath -w "$PWD" 2>/dev/null || printf '%s' "$PWD")"; exit $__rc""";

    public string SnapshotStdout()
    {
        lock (_sync)
            return _stdout.ToString();
    }

    public string SnapshotStderr()
    {
        lock (_sync)
            return _stderr.ToString();
    }

    public string SnapshotTail()
    {
        lock (_sync)
            return _tail.ToString();
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(cancellationToken).ConfigureAwait(false);
        CloseOutputFile();
        DeleteCwdCaptureScript();
        return _limitExceeded ? 1 : _process.ExitCode;
    }

    public void OpenOutputFile(string path)
    {
        lock (_sync)
        {
            if (_outputFile is not null)
                return;
            OutputPath = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _outputFile = _openOutput(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PersistenceFailed = true;
            }
        }
    }

    private async Task PumpAsync(Stream reader, bool stdout, Encoding? legacyEncoding)
    {
        var buffer = new byte[4096];
        var decoder = new BashOutputDecoder(legacyEncoding);
        var pending = "";
        while (true)
        {
            var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
            var text = decoder.Decode(buffer.AsSpan(0, count), flush: count == 0);
            if (text.Length == 0)
            {
                if (count == 0)
                    break;
                continue;
            }
            if (stdout && _captureCwd)
            {
                pending += text;
                // A command may print the marker itself; only the final bounded tail is cwd plumbing.
                var safeLength = Math.Max(0, pending.Length - 32_768);
                if (safeLength > 0 && char.IsHighSurrogate(pending[safeLength - 1]))
                    safeLength--;
                Append(pending[..safeLength], stdout);
                pending = pending[safeLength..];
            }
            else
            {
                // Retain a split surrogate until the next read so persisted UTF-8 stays intact.
                text = pending + text;
                var length = text.Length - (char.IsHighSurrogate(text[^1]) ? 1 : 0);
                Append(text[..length], stdout);
                pending = text[length..];
            }
            if (count == 0)
                break;
        }
        if (stdout && _captureCwd && pending.LastIndexOf(CwdMarker, StringComparison.Ordinal) is >= 0 and var marker)
        {
            _capturedCwd = pending[(marker + CwdMarker.Length)..];
            pending = pending[..marker];
        }
        Append(pending, stdout);
    }

    private void Append(string text, bool stdout)
    {
        if (text.Length == 0)
            return;
        lock (_sync)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            OutputBytes += bytes.Length;
            var target = stdout ? _stdout : _stderr;
            var remaining = InlineLimit - Encoding.UTF8.GetByteCount(target.ToString());
            var chars = 0;
            while (chars < text.Length)
            {
                var size = char.IsSurrogatePair(text, chars) ? 2 : 1;
                var cost = Encoding.UTF8.GetByteCount(text.AsSpan(chars, size));
                if (cost > remaining)
                    break;
                remaining -= cost;
                chars += size;
            }
            target.Append(text.AsSpan(0, chars));
            _tail.Append(text);
            _tailBytes += bytes.Length;
            var removed = 0;
            while (_tailBytes > BashModelContent.MaxInlineOutputBytes && removed < _tail.Length)
            {
                var pair = char.IsHighSurrogate(_tail[removed]) && removed + 1 < _tail.Length && char.IsLowSurrogate(_tail[removed + 1]);
                _tailBytes -= pair ? 4 : _tail[removed] <= 0x7f ? 1 : _tail[removed] <= 0x7ff ? 2 : 3;
                removed += pair ? 2 : 1;
            }
            _tail.Remove(0, removed);
            if (_outputFile is not null)
            {
                try
                {
                    var writable = (int)Math.Min(bytes.Length, Math.Max(0, _persistedLimit - _persistedBytes));
                    _outputFile.Write(bytes.AsSpan(0, writable));
                    _persistedBytes += writable;
                    _outputFile.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PersistenceFailed = true;
                    CloseOutputFile();
                }
            }
            if (OutputBytes > _persistedLimit && !_limitExceeded)
            {
                _limitExceeded = true;
                KillTree();
            }
        }
    }

    public void KillTree()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async Task<int> RunToCompletionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree();
            await WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            CloseOutputFile();
            DeleteCwdCaptureScript();
            _process.Dispose();
        }
    }

    public void TransferOwnership() => _owned = false;

    internal bool TryExtractCapturedCwd(out string captured)
    {
        captured = _capturedCwd?.Trim() ?? "";
        return captured.Length > 0;
    }

    public void DeleteOutputFile()
    {
        CloseOutputFile();
        if (OutputPath is not null)
            File.Delete(OutputPath);
    }

    private void DeleteCwdCaptureScript()
    {
        if (_cwdCaptureScript is null)
            return;
        try
        {
            File.Delete(_cwdCaptureScript);
            _cwdCaptureScript = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void CloseOutputFile()
    {
        lock (_sync)
        {
            try
            {
                _outputFile?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PersistenceFailed = true;
            }
            finally
            {
                _outputFile = null;
            }
        }
    }

    public void Dispose()
    {
        if (!_owned)
            return;
        KillTree();
        try
        {
            Task.WhenAll(_stdoutPump, _stderrPump).GetAwaiter().GetResult();
        }
        finally
        {
            CloseOutputFile();
            DeleteCwdCaptureScript();
            _process.Dispose();
        }
    }
}

internal sealed class BashOutputDecoder(Encoding? legacyEncoding)
{
    private Encoding? _encoding = legacyEncoding is null ? Encoding.UTF8 : null;
    private Decoder? _decoder = legacyEncoding is null ? Encoding.UTF8.GetDecoder() : null;
    private byte[] _pending = [];

    public string Decode(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        if (_decoder is null)
        {
            var combined = new byte[_pending.Length + bytes.Length];
            _pending.CopyTo(combined, 0);
            bytes.CopyTo(combined.AsSpan(_pending.Length));
            var offset = 0;
            var nonAscii = false;
            while (offset < combined.Length)
            {
                var status = Rune.DecodeFromUtf8(combined.AsSpan(offset), out var rune, out var consumed);
                if (status == OperationStatus.NeedMoreData && !flush)
                {
                    if (!nonAscii)
                    {
                        _pending = combined[offset..];
                        return Encoding.UTF8.GetString(combined.AsSpan(0, offset));
                    }
                    _encoding = Encoding.UTF8;
                    _decoder = _encoding.GetDecoder();
                    break;
                }
                if (status != OperationStatus.Done)
                {
                    _encoding = legacyEncoding!;
                    _decoder = _encoding.GetDecoder();
                    break;
                }
                nonAscii |= !rune.IsAscii;
                offset += consumed;
            }
            _pending = [];
            if (_decoder is null)
            {
                if (!nonAscii)
                    return Encoding.UTF8.GetString(combined);
                _encoding = Encoding.UTF8;
                _decoder = _encoding.GetDecoder();
            }
            return Convert(combined, flush);
        }
        return Convert(bytes, flush);
    }

    private string Convert(ReadOnlySpan<byte> bytes, bool flush)
    {
        var chars = new char[_encoding!.GetMaxCharCount(bytes.Length + 4)];
        var written = _decoder!.GetChars(bytes, chars, flush);
        return new string(chars, 0, written);
    }

    internal static Encoding? ResolveWindowsLegacyEncoding()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var configured = Environment.GetEnvironmentVariable("ZCODE_WINDOWS_OUTPUT_ENCODING")?.Trim();
        if (!string.IsNullOrEmpty(configured))
            return Find(configured);
        try
        {
            using var process = new Process
            {
                StartInfo = new(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Arguments = "/d /s /c chcp",
                },
            };
            process.Start();
            if (process.WaitForExit(1_000))
            {
                var output = process.StandardOutput.ReadToEnd();
                var digits = new string(output.SkipWhile(static ch => !char.IsAsciiDigit(ch)).TakeWhile(char.IsAsciiDigit).ToArray());
                if (int.TryParse(digits, out var codePage) && codePage != 65001 && Find($"cp{codePage}") is { } encoding)
                    return encoding;
            }
            else
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
        }
        var locale = string.Join(" ", Environment.GetEnvironmentVariable("LC_ALL"), Environment.GetEnvironmentVariable("LC_CTYPE"),
            Environment.GetEnvironmentVariable("LANG"), CultureInfo.CurrentCulture.Name).ToLowerInvariant();
        return Find(locale.Contains("zh") || locale.Contains("chinese") ? "gb18030"
            : locale.Contains("ja") || locale.Contains("japanese") ? "cp932"
            : locale.Contains("ko") || locale.Contains("korean") ? "cp949"
            : locale.Contains("ru") || locale.Contains("russian") ? "cp866" : "cp437");

        static Encoding? Find(string name)
        {
            try
            {
                return name.StartsWith("cp", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(2), out var codePage)
                    ? Encoding.GetEncoding(codePage) : Encoding.GetEncoding(name);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}

internal sealed record BashTimeoutPolicy(int DefaultMs, int MaxMs)
{
    public static BashTimeoutPolicy FromEnvironment(Func<string, string?> get)
    {
        var defaultMs = ParsePositive(get("BASH_DEFAULT_TIMEOUT_MS")) ?? BashTool.DefaultTimeoutMs;
        return new(defaultMs, Math.Max(defaultMs, ParsePositive(get("BASH_MAX_TIMEOUT_MS")) ?? BashTool.MaxTimeoutMs));
    }

    public int Resolve(int? input) => Math.Min(input is null or 0 ? DefaultMs : input.Value, MaxMs);

    internal static int? ParsePositive(string? value)
    {
        var text = value?.TrimStart();
        if (string.IsNullOrEmpty(text))
            return null;
        var end = text[0] is '+' or '-' ? 1 : 0;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;
        return int.TryParse(text.AsSpan(0, end), out var number) && number > 0 ? number : null;
    }
}
