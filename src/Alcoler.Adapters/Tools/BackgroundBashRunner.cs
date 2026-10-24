using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Alcoler.Adapters.Tools;

internal sealed class BashProcessSession : IDisposable
{
    internal const string CwdMarker = "__ALCOLER_CWD__";
    internal const long MaxPersistedBytes = 5L * 1024 * 1024 * 1024;

    // The console codepage cannot change within the process, so the chcp probe runs at most once.
    private static readonly Lazy<Encoding?> LegacyEncodingCache = new(BashOutputDecoder.ResolveWindowsLegacyEncoding);

    private readonly Process _process;
    private readonly object _sync = new();
    private const int CwdTailChars = 32_768;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _tail = new();
    private readonly bool _captureCwd;
    private readonly long _persistedLimit;
    private readonly Func<string, Stream> _openOutput;
    private Stream? _outputFile;
    private long _persistedBytes;
    private int _stdoutBytes;
    private int _stderrBytes;
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
        _openOutput = openOutput ?? (path => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 32_768));
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
            // An inherited stdin lets cmd treat batch lines as interactive input (exit 255)
            // and lets commands steal the host console; EOF keeps execution non-interactive.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var posix = isPosix ?? IsPosixShell(shell);
        legacyEncoding ??= LegacyEncodingCache.Value;
        var session = new BashProcessSession(new Process { StartInfo = startInfo }, captureCwd,
            ResolveInlineLimit(Environment.GetEnvironmentVariable("BASH_MAX_OUTPUT_LENGTH")), persistedLimit, openOutput);
        try
        {
            if (posix)
            {
                startInfo.ArgumentList.Add(shell.Arguments);
                startInfo.ArgumentList.Add(
                    "exec 2>&1; " + BashRipgrepShim.Preamble() + (captureCwd ? WrapWithCwdCapture(command) : command));
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
            session._process.StandardInput.Close();
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
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var decoder = new BashOutputDecoder(legacyEncoding);
        var capturePending = stdout && _captureCwd ? ArrayPool<char>.Shared.Rent(CwdTailChars + 16_384) : null;
        var combined = capturePending is null ? ArrayPool<char>.Shared.Rent(8_192) : null;
        var pendingCount = 0;
        var deferredSurrogate = '\0';
        try
        {
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
                if (capturePending is not null)
                {
                    AppendCwdText(capturePending, ref pendingCount, text);
                }
                else
                {
                    // Retain a split surrogate until the next read so persisted UTF-8 stays intact.
                    if (combined!.Length < text.Length + 1)
                    {
                        ArrayPool<char>.Shared.Return(combined);
                        combined = ArrayPool<char>.Shared.Rent(text.Length + 1);
                    }
                    var offset = 0;
                    if (deferredSurrogate != '\0')
                    {
                        combined[0] = deferredSurrogate;
                        offset = 1;
                        deferredSurrogate = '\0';
                    }
                    text.AsSpan().CopyTo(combined.AsSpan(offset));
                    var length = offset + text.Length - (char.IsHighSurrogate(combined[offset + text.Length - 1]) ? 1 : 0);
                    Append(combined.AsSpan(0, length), stdout);
                    if (length < offset + text.Length)
                        deferredSurrogate = combined[offset + text.Length - 1];
                }
                if (count == 0)
                    break;
            }
            if (capturePending is not null)
            {
                // A command may print the marker itself; only the final bounded tail is cwd plumbing.
                var pending = capturePending.AsSpan(0, pendingCount);
                var marker = pending.LastIndexOf(CwdMarker.AsSpan(), StringComparison.Ordinal);
                if (marker >= 0)
                {
                    _capturedCwd = new string(pending[(marker + CwdMarker.Length)..]);
                    pending = pending[..marker];
                }
                Append(pending, stdout);
            }
            else if (deferredSurrogate != '\0')
            {
                var trailing = combined!;
                trailing[0] = deferredSurrogate;
                Append(trailing.AsSpan(0, 1), stdout);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (capturePending is not null)
                ArrayPool<char>.Shared.Return(capturePending);
            if (combined is not null)
                ArrayPool<char>.Shared.Return(combined);
        }
    }

    private void AppendCwdText(char[] pending, ref int pendingCount, string text)
    {
        text.AsSpan().CopyTo(pending.AsSpan(pendingCount));
        pendingCount += text.Length;
        if (pendingCount <= CwdTailChars)
            return;
        var safeLength = pendingCount - CwdTailChars;
        if (char.IsHighSurrogate(pending[safeLength - 1]))
            safeLength--;
        if (safeLength <= 0)
            return;
        Append(pending.AsSpan(0, safeLength), stdout: true);
        pending.AsSpan(safeLength, pendingCount - safeLength).CopyTo(pending);
        pendingCount -= safeLength;
    }

    private void Append(ReadOnlySpan<char> text, bool stdout)
    {
        if (text.IsEmpty)
            return;
        lock (_sync)
        {
            var byteCount = Encoding.UTF8.GetByteCount(text);
            OutputBytes += byteCount;
            var target = stdout ? _stdout : _stderr;
            var remaining = InlineLimit - (stdout ? _stdoutBytes : _stderrBytes);
            var chars = 0;
            while (chars < text.Length)
            {
                var size = chars + 1 < text.Length && char.IsSurrogatePair(text[chars], text[chars + 1]) ? 2 : 1;
                // Non-ASCII single chars keep GetByteCount: a lone surrogate counts via the encoder's replacement fallback.
                var cost = size == 2 ? 4 : text[chars] <= 0x7F ? 1 : Encoding.UTF8.GetByteCount(text.Slice(chars, 1));
                if (cost > remaining)
                    break;
                remaining -= cost;
                chars += size;
            }
            target.Append(text[..chars]);
            if (stdout)
                _stdoutBytes = InlineLimit - remaining;
            else
                _stderrBytes = InlineLimit - remaining;
            _tail.Append(text);
            _tailBytes += byteCount;
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
                    var writable = (int)Math.Min(byteCount, Math.Max(0, _persistedLimit - _persistedBytes));
                    if (writable > 0)
                    {
                        var encoded = ArrayPool<byte>.Shared.Rent(byteCount);
                        try
                        {
                            Encoding.UTF8.GetBytes(text, encoded);
                            _outputFile.Write(encoded.AsSpan(0, writable));
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(encoded);
                        }
                    }
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
    private char[] _chars = [];

    public string Decode(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        if (_decoder is null)
        {
            var combined = CombinePending(bytes);
            var offset = 0;
            var nonAscii = false;
            while (offset < combined.Length)
            {
                var status = Rune.DecodeFromUtf8(combined[offset..], out var rune, out var consumed);
                if (status == OperationStatus.NeedMoreData && !flush)
                {
                    if (!nonAscii)
                    {
                        _pending = combined[offset..].ToArray();
                        return Encoding.UTF8.GetString(combined[..offset]);
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

    // With no pending bytes the caller's span is already the combined buffer, so nothing is copied.
    private ReadOnlySpan<byte> CombinePending(ReadOnlySpan<byte> bytes)
    {
        if (_pending.Length == 0)
            return bytes;
        var copy = new byte[_pending.Length + bytes.Length];
        _pending.CopyTo(copy, 0);
        bytes.CopyTo(copy.AsSpan(_pending.Length));
        return copy;
    }

    private string Convert(ReadOnlySpan<byte> bytes, bool flush)
    {
        var required = _encoding!.GetMaxCharCount(bytes.Length + 4);
        if (_chars.Length < required)
            _chars = new char[required];
        var written = _decoder!.GetChars(bytes, _chars, flush);
        return new string(_chars, 0, written);
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
