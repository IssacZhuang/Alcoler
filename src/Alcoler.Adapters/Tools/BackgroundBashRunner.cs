using System.Diagnostics;
using System.Text;

namespace Alcoler.Adapters.Tools;

/// <summary>
/// One shell process with stdout/stderr captured per stream plus a combined log that can
/// stream into an output file while the process runs. The session survives the tool call
/// that started it so a timed-out command can be handed to the background job service.
/// </summary>
internal sealed class BashProcessSession : IDisposable
{
    internal const string CwdMarker = "__ALCOLER_CWD__";

    private readonly Process _process;
    private readonly object _sync = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _combined = new();
    private FileStream? _outputFile;
    private readonly bool _captureCwd;
    private bool _owned = true;

    private BashProcessSession(Process process, bool captureCwd)
    {
        _process = process;
        _captureCwd = captureCwd;
    }

    public static BashProcessSession Start(
        ShellSpec shell,
        string command,
        string workingDirectory,
        bool captureCwd,
        string? outputFilePath)
    {
        var startInfo = new ProcessStartInfo(shell.FileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(shell.Arguments);
        startInfo.ArgumentList.Add(captureCwd ? WrapWithCwdCapture(command) : command);

        var process = new Process { StartInfo = startInfo };
        var session = new BashProcessSession(process, captureCwd);
        if (outputFilePath is not null)
            session.OpenOutputFile(outputFilePath);

        process.OutputDataReceived += session.OnStdoutLine;
        process.ErrorDataReceived += session.OnStderrLine;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return session;
    }

    /// <summary>
    /// Posix-shell wrapper that keeps the command's exit status while appending the final
    /// working directory as a stdout marker; the marker is stripped before the model sees
    /// the output. cygpath yields a Windows path from git-bash (whose $PWD may use an MSYS
    /// mount like /tmp that has no drive-letter mapping); shells without cygpath fall back
    /// to the native $PWD.
    /// </summary>
    private static string WrapWithCwdCapture(string command) =>
        $$"""{ {{command}} ; } ; __rc=$?; printf '{{CwdMarker}}%s' "$(cygpath -w "$PWD" 2>/dev/null || printf '%s' "$PWD")"; exit $__rc""";

    public string SnapshotStdout()
    {
        lock (_sync)
        {
            return StripCwdMarker(_stdout.ToString());
        }
    }

    public string SnapshotStderr()
    {
        lock (_sync)
        {
            return _stderr.ToString();
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        // WaitForExit() without a timeout flushes the async output event queue.
        _process.WaitForExit();
        return _process.ExitCode;
    }

    /// <summary>
    /// Moves a still-running foreground process to a background output file; everything
    /// captured so far is flushed before live output continues into the file.
    /// </summary>
    public void OpenOutputFile(string path)
    {
        lock (_sync)
        {
            if (_outputFile is not null)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _outputFile = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096);
            var prefix = Encoding.UTF8.GetBytes(_combined.ToString());
            _outputFile.Write(prefix, 0, prefix.Length);
            _outputFile.Flush();
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
            var exitCode = await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            KillTree();
            try
            {
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        finally
        {
            CloseOutputFile();
            _process.Dispose();
        }
    }

    /// <summary>
    /// Detaches the session from its creating scope; the background job's completion
    /// callback becomes responsible for releasing process and file handles.
    /// </summary>
    public void TransferOwnership() => _owned = false;

    private void OnStdoutLine(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
            return;
        lock (_sync)
        {
            _stdout.AppendLine(e.Data);
            AppendCombined(e.Data);
        }
    }

    private void OnStderrLine(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
            return;
        lock (_sync)
        {
            _stderr.AppendLine(e.Data);
            AppendCombined(e.Data);
        }
    }

    private void AppendCombined(string line)
    {
        // The cwd marker is tool plumbing, not command output, and must not reach the
        // background output file the model reads.
        if (_captureCwd && line.StartsWith(CwdMarker, StringComparison.Ordinal))
            return;
        _combined.AppendLine(line);
        if (_outputFile is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            _outputFile.Write(bytes, 0, bytes.Length);
            _outputFile.Flush();
        }
    }

    private string StripCwdMarker(string stdout)
    {
        if (!_captureCwd)
            return stdout;
        var markerIndex = stdout.LastIndexOf(CwdMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return stdout;
        return stdout[..markerIndex];
    }

    internal bool TryExtractCapturedCwd(out string captured)
    {
        captured = string.Empty;
        if (!_captureCwd)
            return false;
        string stdout;
        lock (_sync)
        {
            stdout = _stdout.ToString();
        }
        var markerIndex = stdout.LastIndexOf(CwdMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;
        captured = stdout[(markerIndex + CwdMarker.Length)..].Trim();
        return captured.Length > 0;
    }

    private void CloseOutputFile()
    {
        lock (_sync)
        {
            _outputFile?.Dispose();
            _outputFile = null;
        }
    }

    public void Dispose()
    {
        if (!_owned)
            return;
        CloseOutputFile();
        _process.Dispose();
    }
}
