using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Alcoler.Audio;

public enum TaskAlert
{
    TaskCompleted,
    AwaitingConfirmation,
}

public interface ITaskAlertSounds
{
    void Play(TaskAlert alert);
}

internal sealed class NoopTaskAlertSounds : ITaskAlertSounds
{
    public static readonly NoopTaskAlertSounds Instance = new();

    public void Play(TaskAlert alert)
    {
    }
}

// Sounds are embedded 16-bit PCM WAVs. Windows plays them in-memory through winmm with
// SND_ASYNC, which requires the buffer to stay valid for the whole playback, so the samples
// are pinned once per process. Other platforms fall back to the system audio utility; a
// missing player or any failure is silent because an alert must never surface in the UI.
internal sealed class TaskAlertSounds : ITaskAlertSounds
{
    public static readonly TaskAlertSounds Instance = new();

    private readonly Dictionary<string, GCHandle> _pinned = [];
    private readonly Dictionary<string, string> _extracted = [];

    public void Play(TaskAlert alert)
    {
        try
        {
            var name = alert switch
            {
                TaskAlert.TaskCompleted => "TaskCompleted",
                _ => "AwaitingConfirmation",
            };
            if (OperatingSystem.IsWindows())
                PlayWindows(name);
            else
                PlayUnix(name);
        }
        catch
        {
            // Playback is best effort by design.
        }
    }

    private byte[] LoadSamples(string name)
    {
        using var stream = typeof(TaskAlertSounds).Assembly
            .GetManifestResourceStream($"Alcoler.Assets.Sounds.{name}.wav");
        if (stream is null)
            throw new InvalidOperationException($"Embedded alert sound '{name}' was not found.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private void PlayWindows(string name)
    {
        if (!_pinned.TryGetValue(name, out var pinned))
        {
            pinned = GCHandle.Alloc(LoadSamples(name), GCHandleType.Pinned);
            _pinned[name] = pinned;
        }
        PlaySound(pinned.AddrOfPinnedObject(), IntPtr.Zero, SNDAsync | SNDMemory);
    }

    private void PlayUnix(string name)
    {
        var path = ExtractSound(name);
        if (path is null)
            return;
        if (OperatingSystem.IsMacOS())
        {
            TrySpawnPlayer("afplay", Quote(path));
            return;
        }
        if (!TrySpawnPlayer("paplay", Quote(path)) && !TrySpawnPlayer("aplay", Quote(path)))
            TrySpawnPlayer("ffplay", $"-nodisp -autoexit -loglevel quiet {Quote(path)}");
    }

    private string? ExtractSound(string name)
    {
        if (_extracted.TryGetValue(name, out var cached) && File.Exists(cached))
            return cached;
        var directory = Path.Combine(Path.GetTempPath(), "alcoler-alerts");
        var path = Path.Combine(directory, $"{name}.wav");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, LoadSamples(name));
        _extracted[name] = path;
        return path;
    }

    // Starting synchronously keeps the not-found failure observable for the fallback chain;
    // only the exit wait runs in the background.
    private static bool TrySpawnPlayer(string fileName, string arguments)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(fileName)
            {
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch
        {
            return false;
        }
        if (process is null)
            return false;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            finally
            {
                process.Dispose();
            }
        });
        return true;
    }

    private static string Quote(string path) => $"\"{path}\"";

    private const int SNDAsync = 0x1;
    private const int SNDMemory = 0x4;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, int flags);
}
