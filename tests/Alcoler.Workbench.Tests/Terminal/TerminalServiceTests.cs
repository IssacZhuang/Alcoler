using Alcoler.Workbench.Terminal;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Porta.Pty;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Workbench.Tests.Terminal;

/// <summary>
/// Real PTY round-trip. All waits are bounded; if the environment cannot spawn a
/// PTY these fail on timeout rather than hang.
/// </summary>
public class TerminalServiceTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Shell_Spawns_EchoesBack_AndExits()
    {
        using var service = new PortaPtyTerminalService();
        var output = new LockFreeAppend();

        var firstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OutputReceived += OnChunk;
        service.Exited += (_, code) => exited.TrySetResult(code);

        try
        {
            await service.StartAsync(Path.GetTempPath(), 120, 30, CancellationToken.None);
            Assert.True(service.IsRunning);

            var first = await Task.WhenAny(firstChunk.Task, Task.Delay(WaitLimit));
            Assert.True(first == firstChunk.Task, "No output chunk arrived within 10 seconds of spawn.");

            var marker = "alcoler_pty_probe_" + Guid.NewGuid().ToString("N")[..6];
            await service.WriteAsync("echo " + marker + "\r", CancellationToken.None);
            Assert.True(
                await WaitForAsync(() => output.Text.Contains(marker, StringComparison.Ordinal)),
                "Marker not observed in PTY output within 10 seconds.");

            await service.WriteAsync("exit\r", CancellationToken.None);
            var finished = await Task.WhenAny(exited.Task, Task.Delay(WaitLimit));
            Assert.True(finished == exited.Task, "Shell did not exit within 10 seconds of 'exit'.");
        }
        finally
        {
            service.OutputReceived -= OnChunk;
            await service.StopAsync();
        }

        void OnChunk(object? sender, string chunk)
        {
            output.Append(chunk);
            firstChunk.TrySetResult();
        }
    }

    [Fact]
    public async Task ViewModel_StartsWithFittedDimensions_AndResizesIfViewportChangesDuringStart()
    {
        using var service = new FakePtyService
        {
            StartCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var viewModel = new TerminalPaneViewModel(@"C:\ws\project", service, InlineUiScheduler.Instance);
        viewModel.Resize(80, 18);
        var start = viewModel.StartAsync();
        Assert.Equal((80, 18), service.StartedSize);
        viewModel.Resize(90, 20);
        Assert.Empty(service.Resizes);

        service.StartCompletion!.SetResult();
        await start;

        Assert.True(viewModel.IsConnected);
        Assert.Equal((90, 20), Assert.Single(service.Resizes));
        viewModel.Resize(90, 20);
        Assert.Single(service.Resizes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_DisposedOrCanceledDuringSpawn_KillsAndDisposesLateConnection(bool cancel)
    {
        var spawn = new TaskCompletionSource<IPtyConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new PortaPtyTerminalService((_, _) => spawn.Task);
        using var cancellation = new CancellationTokenSource();
        var connection = new FakePtyConnection();
        var start = service.StartAsync(Path.GetTempPath(), 80, 24, cancellation.Token);
        Assert.False(start.IsCompleted);
        if (cancel)
        {
            cancellation.Cancel();
        }
        else
        {
            service.Dispose();
        }

        spawn.SetResult(connection);

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        }
        else
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => start);
        }
        Assert.False(service.IsRunning);
        Assert.Equal(1, connection.KillCount);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(0, connection.SubscriberCount);
        Assert.Equal(0, connection.ReaderAccessCount);
        service.Dispose();
        Assert.Equal(1, connection.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewModel_DisposedDuringStart_DoesNotConnectOrResizeAfterCompletion(bool throwOnCompletion)
    {
        using var service = new FakePtyService
        {
            StartCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var viewModel = new TerminalPaneViewModel(@"C:\ws\project", service, InlineUiScheduler.Instance);
        var connectedChanges = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalPaneViewModel.IsConnected))
            {
                connectedChanges++;
            }
        };
        var start = viewModel.StartAsync();
        viewModel.Resize(80, 24);
        viewModel.Dispose();
        if (throwOnCompletion)
        {
            service.StartCompletion!.SetException(new ObjectDisposedException(nameof(FakePtyService)));
        }
        else
        {
            service.StartCompletion!.SetResult();
        }

        await start;

        Assert.False(viewModel.IsConnected);
        Assert.Equal(0, connectedChanges);
        Assert.Empty(service.Resizes);
        Assert.Equal(1, service.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => viewModel.StartAsync());
        viewModel.Dispose();
        Assert.Equal(1, service.DisposeCount);
    }

    [AvaloniaFact]
    public async Task TerminalView_FitsSurface_ForwardsInputAndToolbarActions_AndRebinds()
    {
        using var service = new FakePtyService();
        using var viewModel = new TerminalPaneViewModel(@"C:\ws\project", service, InlineUiScheduler.Instance);
        var view = new TerminalView { DataContext = viewModel };
        var window = new Window { Width = 600, Height = 300, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var surface = view.FindControl<TerminalSurface>("Surface")!;
            var toolbar = view.FindControl<Grid>("PART_TerminalToolbar")!;
            Assert.Equal(28, toolbar.Height);
            Assert.Equal("project", viewModel.WorkspaceName);
            Assert.True(surface.Bounds.Width > 0);
            Assert.True(surface.Bounds.Height > 0);
            var fitted = (viewModel.Buffer.Cols, viewModel.Buffer.Rows);
            view.FindControl<Button>("PART_TerminalStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(viewModel.IsConnected);
            Assert.Equal(fitted, service.StartedSize);

            surface.FontSize = 20;
            Assert.True(viewModel.Buffer.Cols <= fitted.Cols);
            Assert.True(viewModel.Buffer.Rows < fitted.Rows);
            Assert.Equal((viewModel.Buffer.Cols, viewModel.Buffer.Rows), service.Resizes[^1]);
            var smaller = (viewModel.Buffer.Cols, viewModel.Buffer.Rows);
            window.Width = 900;
            window.Height = 480;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.Buffer.Cols > smaller.Cols);
            Assert.True(viewModel.Buffer.Rows > smaller.Rows);
            Assert.Equal((viewModel.Buffer.Cols, viewModel.Buffer.Rows), service.Resizes[^1]);
            var resizeCount = service.Resizes.Count;
            surface.ResizeToViewport(new Size(0, 0));
            Assert.Equal(resizeCount, service.Resizes.Count);

            surface.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "echo test" });
            surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(new[] { "echo test", "\r" }, service.Writes);
            service.Emit("output");
            Assert.Equal('o', viewModel.Buffer.GetRow(0)[0].Char);
            view.FindControl<Button>("PART_TerminalClear")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(' ', viewModel.Buffer.GetRow(0)[0].Char);
            view.FindControl<Button>("StopButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(viewModel.IsConnected);
            Assert.Equal(1, service.StopCount);

            using var nextService = new FakePtyService();
            using var next = new TerminalPaneViewModel(@"C:\ws\next", nextService, InlineUiScheduler.Instance);
            view.DataContext = next;
            Assert.Same(next, surface.ViewModel);
            Assert.Same(next.Buffer, surface.Buffer);
            Assert.Equal((viewModel.Buffer.Cols, viewModel.Buffer.Rows), (next.Buffer.Cols, next.Buffer.Rows));
            await next.StartAsync();
            Assert.Equal((next.Buffer.Cols, next.Buffer.Rows), nextService.StartedSize);
            view.DataContext = null;
            Assert.Null(surface.ViewModel);
            Assert.Null(surface.Buffer);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class FakePtyConnection : IPtyConnection
    {
        public event EventHandler<PtyExitedEventArgs>? ProcessExited;

        public int SubscriberCount => ProcessExited?.GetInvocationList().Length ?? 0;

        public int KillCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int ReaderAccessCount { get; private set; }

        public Stream ReaderStream
        {
            get
            {
                ReaderAccessCount++;
                return Stream.Null;
            }
        }

        public Stream WriterStream => Stream.Null;

        public int Pid => 1;

        public int ExitCode => 0;

        public bool WaitForExit(int milliseconds) => true;

        public void Kill() => KillCount++;

        public void Resize(int cols, int rows)
        {
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakePtyService : IPtyTerminalService
    {
        public bool IsRunning { get; private set; }

        public event EventHandler<string>? OutputReceived;

        public event EventHandler<int>? Exited;

        public (int Cols, int Rows) StartedSize { get; private set; }

        public List<(int Cols, int Rows)> Resizes { get; } = [];

        public List<string> Writes { get; } = [];

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public TaskCompletionSource? StartCompletion { get; init; }

        public async Task StartAsync(string workingDirectory, int cols, int rows, CancellationToken cancellationToken)
        {
            StartedSize = (cols, rows);
            if (StartCompletion is not null)
            {
                await StartCompletion.Task;
            }
            IsRunning = true;
        }

        public Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            Writes.Add(text);
            return Task.CompletedTask;
        }

        public void Resize(int cols, int rows) => Resizes.Add((cols, rows));

        public Task StopAsync()
        {
            StopCount++;
            IsRunning = false;
            Exited?.Invoke(this, 0);
            return Task.CompletedTask;
        }

        public void Emit(string output) => OutputReceived?.Invoke(this, output);

        public void Dispose() => DisposeCount++;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(100);
        }
        return condition();
    }

    private sealed class LockFreeAppend
    {
        private readonly object _gate = new();
        private readonly System.Text.StringBuilder _builder = new();

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _builder.ToString();
                }
            }
        }

        public void Append(string chunk)
        {
            lock (_gate)
            {
                _builder.Append(chunk);
            }
        }
    }
}
