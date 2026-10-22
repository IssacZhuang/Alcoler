using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using Alcoler.Browser;
using Alcoler.Contracts.Identifiers;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Alcoler.Browser;

internal sealed partial class WebViewBrowserPage : IBrowserPage
{
    private static readonly string[] ProtocolMethods =
    [
        "Page.frameNavigated", "Page.frameAttached", "Page.frameDetached", "Page.frameStartedLoading",
        "Page.frameStoppedLoading", "Page.domContentEventFired", "Page.loadEventFired", "Page.lifecycleEvent",
        "Page.navigatedWithinDocument", "Page.windowOpen", "Page.fileChooserOpened",
        "Runtime.executionContextCreated", "Runtime.executionContextDestroyed", "Runtime.executionContextsCleared",
        "Runtime.consoleAPICalled", "Runtime.exceptionThrown", "Runtime.bindingCalled",
        "Network.requestWillBeSent", "Network.responseReceived", "Network.loadingFinished", "Network.loadingFailed",
        "Network.requestWillBeSentExtraInfo", "Network.responseReceivedExtraInfo",
        "Target.attachedToTarget", "Target.detachedFromTarget", "Target.targetCreated", "Target.targetDestroyed",
        "Target.targetInfoChanged", "Target.receivedMessageFromTarget",
        "DOM.documentUpdated", "DOM.childNodeInserted", "DOM.childNodeRemoved", "DOM.attributeModified",
        "Inspector.targetCrashed", "Inspector.detached", "Log.entryAdded", "Fetch.requestPaused", "Fetch.authRequired"
    ];

    private readonly WebViewBrowserHost _host;
    private readonly string _userDataFolder;
    private readonly string _profileName;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(NativeComObject<ICdpEventReceiver> Receiver, long Token, CdpEventCallback Callback)> _receivers = [];
    private readonly List<Action> _unregister = [];
    private readonly Dictionary<long, CdpCompletedCallback> _pending = [];
    private readonly NativeCallbacks _callbacks;
    private readonly string _downloadRoot;
    private readonly Dictionary<string, NativeDownload> _downloads = new(StringComparer.Ordinal);
    private string? _downloadDirectory;
    private NativeComObject<ICoreWebView2>? _native;
    private NativeComObject<IScriptDialogArgs>? _dialog;
    private NativeComObject<IWebViewDeferral>? _dialogDeferral;
    private DispatcherTimer? _dialogTimer;
    private long _nextCommand;
    private bool _disposed;

    public WebViewBrowserPage(WebViewBrowserHost host, SessionId sessionId, string id, string userDataFolder, string profileName)
    {
        _host = host;
        SessionId = sessionId;
        Id = id;
        _userDataFolder = userDataFolder;
        _profileName = profileName;
        _downloadRoot = Path.Combine(userDataFolder, "downloads",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profileName + ":" + id))));
        _downloadDirectory = _downloadRoot;
        _callbacks = new NativeCallbacks(this);
        Dispatcher.UIThread.UnhandledException += OnInitializationException;
        WebView = new NativeWebView();
        WebView.EnvironmentRequested += OnEnvironmentRequested;
        WebView.AdapterCreated += OnAdapterCreated;
        WebView.AdapterDestroyed += OnAdapterDestroyed;
        WebView.NavigationStarted += OnNavigationStarted;
        WebView.NavigationCompleted += OnNavigationCompleted;
    }

    public string Id { get; }
    public SessionId SessionId { get; }
    public NativeWebView WebView { get; }
    public Task Ready => _ready.Task;
    public event EventHandler<BrowserProtocolEvent>? ProtocolEvent;

    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken, string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        await Ready.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        var json = BrowserJson.Serialize(parameters ?? new Dictionary<string, object?>());
        var result = await WebViewBrowserHost.OnUiAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (method is "Page.navigate" or "Page.navigateToHistoryEntry")
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("url", out var url) && !WebViewBrowserHost.IsAllowedUrl(url.GetString()))
                {
                    throw new InvalidOperationException("Browser navigation permits only http, https, about, and data URLs.");
                }
            }
            if (method == "Page.handleJavaScriptDialog" && _dialog is not null && sessionId is null)
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                CompleteDialog(root.TryGetProperty("accept", out var accept) && accept.GetBoolean(),
                    root.TryGetProperty("promptText", out var prompt) ? prompt.GetString() : null);
                return Task.FromResult(BrowserJson.Element(new Dictionary<string, object?>()));
            }
            if (method is "Browser.setDownloadBehavior" or "Page.setDownloadBehavior" && sessionId is null)
            {
                using var document = JsonDocument.Parse(json);
                ConfigureDownloads(document.RootElement);
                return Task.FromResult(BrowserJson.Element(new Dictionary<string, object?>()));
            }
            if (method == "Browser.cancelDownload" && sessionId is null)
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("guid", out var guid)
                    && _downloads.TryGetValue(guid.GetString() ?? "", out var download))
                {
                    download.Operation.Value.Cancel();
                }
                return Task.FromResult(BrowserJson.Element(new Dictionary<string, object?>()));
            }
            if (_pending.Count >= 256)
            {
                throw new InvalidOperationException("Too many native browser commands are awaiting completion.");
            }
            var commandId = ++_nextCommand;
            var callback = new CdpCompletedCallback(this, commandId);
            _pending.Add(commandId, callback);
            try
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    _native!.Value.CallDevToolsProtocolMethod(method, json, callback);
                }
                else if (_native!.Value is ICoreWebView2_11 forSession)
                {
                    forSession.CallDevToolsProtocolMethodForSession(sessionId, method, json, callback);
                }
                else
                {
                    throw new NotSupportedException("This WebView2 Runtime does not support CDP commands for out-of-process frames.");
                }
            }
            catch (Exception ex)
            {
                _pending.Remove(commandId);
                callback.Completion.TrySetException(ex);
            }
            return callback.Completion.Task;
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            return await result.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            await DrainCanceledCommandAsync(result, method, sessionId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DrainCanceledCommandAsync(Task<JsonElement> original, string method, string? sessionId)
    {
        if (original.IsCompleted)
        {
            return;
        }
        var terminate = await WebViewBrowserHost.OnUiAsync(() =>
        {
            if (_disposed || original.IsCompleted || method is not ("Runtime.evaluate" or "Runtime.callFunctionOn" or "Runtime.runScript"))
            {
                return Task.CompletedTask;
            }
            var id = ++_nextCommand;
            var callback = new CdpCompletedCallback(this, id);
            _pending.Add(id, callback);
            try
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    _native!.Value.CallDevToolsProtocolMethod("Runtime.terminateExecution", "{}", callback);
                }
                else if (_native!.Value is ICoreWebView2_11 forSession)
                {
                    forSession.CallDevToolsProtocolMethodForSession(sessionId, "Runtime.terminateExecution", "{}", callback);
                }
            }
            catch (Exception ex)
            {
                _pending.Remove(id);
                callback.Completion.TrySetException(ex);
            }
            return (Task)callback.Completion.Task;
        }, CancellationToken.None).ConfigureAwait(false);
        var drain = Task.WhenAll(original, terminate);
        try
        {
            await drain.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // An unacknowledged native command must not outlive the operation gate.
            await _host.ClosePageAsync(Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!drain.IsCompleted)
            {
                await _host.ClosePageAsync(Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void OnInitializationException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_ready.Task.IsCompleted || e.Exception.StackTrace?.Contains("Avalonia.Controls.Win.WebView2.", StringComparison.Ordinal) != true)
        {
            return;
        }
        e.Handled = true;
        _ready.TrySetException(new InvalidOperationException($"WebView2 initialization failed: {e.Exception.Message}", e.Exception));
        Dispatcher.UIThread.UnhandledException -= OnInitializationException;
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is not WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            _ready.TrySetException(new PlatformNotSupportedException("Microsoft Edge WebView2 is required."));
            return;
        }
        windows.UserDataFolder = _userDataFolder;
        windows.ProfileName = _profileName;
        windows.EnableDevTools = false;
        windows.AllowSingleSignOnUsingOSPrimaryAccount = false;
        windows.AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding --no-first-run";
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        try
        {
            if (_disposed)
            {
                WebView.AdapterCreated -= OnAdapterCreated;
                if (e.TryGetPlatformHandle() is IDisposable lateAdapter)
                {
                    TryNative(lateAdapter.Dispose);
                }
                return;
            }
            if (e.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle windows)
            {
                throw new InvalidOperationException("The WebView adapter is not WebView2. Install the Evergreen WebView2 Runtime.");
            }
            _native = new NativeComObject<ICoreWebView2>(windows.CoreWebView2, true);
            if (_native.Value is not ICoreWebView2_13 profiles)
            {
                throw new NotSupportedException("Update the WebView2 Runtime: isolated browser profiles are required.");
            }
            profiles.GetProfile(out var profilePointer);
            using (var profile = new NativeComObject<IWebViewProfile>(profilePointer, true))
            {
                profile.Value.GetProfileName(out var actualName);
                if (!string.Equals(actualName, _profileName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("WebView2 did not create the requested isolated session profile.");
                }
            }
            _native.Value.GetSettings(out var settingsPointer);
            using (var settings = new NativeComObject<IWebViewSettings>(settingsPointer, true))
            {
                settings.Value.SetAreHostObjectsAllowed(0);
                settings.Value.SetIsWebMessageEnabled(0);
                settings.Value.SetAreDefaultScriptDialogsEnabled(0);
                settings.Value.SetIsStatusBarEnabled(0);
            }
            RegisterNativeCallbacks();
            foreach (var method in ProtocolMethods)
            {
                _native.Value.GetDevToolsProtocolEventReceiver(method, out var pointer);
                var receiver = new NativeComObject<ICdpEventReceiver>(pointer, true);
                var callback = new CdpEventCallback(this, method);
                try
                {
                    receiver.Value.AddDevToolsProtocolEventReceived(callback, out var token);
                    _receivers.Add((receiver, token, callback));
                }
                catch
                {
                    receiver.Dispose();
                    throw;
                }
            }
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(new InvalidOperationException($"WebView2 initialization failed: {ex.Message}", ex));
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnInitializationException;
        }
    }

    private void RegisterNativeCallbacks()
    {
        var core = _native!.Value;
        core.AddNewWindowRequested(_callbacks, out var newWindow);
        _unregister.Add(() => core.RemoveNewWindowRequested(newWindow));
        core.AddScriptDialogOpening(_callbacks, out var dialog);
        _unregister.Add(() => core.RemoveScriptDialogOpening(dialog));
        core.AddPermissionRequested(_callbacks, out var permission);
        _unregister.Add(() => core.RemovePermissionRequested(permission));
        core.AddFrameNavigationStarting(_callbacks, out var frameNavigation);
        _unregister.Add(() => core.RemoveFrameNavigationStarting(frameNavigation));
        core.AddProcessFailed(_callbacks, out var process);
        _unregister.Add(() => core.RemoveProcessFailed(process));
        core.AddDocumentTitleChanged(_callbacks, out var title);
        _unregister.Add(() => core.RemoveDocumentTitleChanged(title));
        core.AddWindowCloseRequested(_callbacks, out var close);
        _unregister.Add(() => core.RemoveWindowCloseRequested(close));
        if (core is ICoreWebView2_4 downloadCore)
        {
            downloadCore.AddDownloadStarting(_callbacks, out var download);
            _unregister.Add(() => downloadCore.RemoveDownloadStarting(download));
        }
        if (core is ICoreWebView2_18 externalCore)
        {
            externalCore.AddLaunchingExternalUriScheme(_callbacks, out var external);
            _unregister.Add(() => externalCore.RemoveLaunchingExternalUriScheme(external));
        }
        else
        {
            throw new NotSupportedException("Update the WebView2 Runtime: external URI launch protection is required.");
        }
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (!WebViewBrowserHost.IsAllowedUrl(e.Request?.AbsoluteUri))
        {
            e.Cancel = true;
            Raise("Host.navigationBlocked", new Dictionary<string, object?> { ["url"] = e.Request?.AbsoluteUri });
        }
        else
        {
            Raise("Host.loadingChanged", new Dictionary<string, object?> { ["isLoading"] = true, ["url"] = e.Request?.AbsoluteUri });
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e) =>
        Raise("Host.loadingChanged", new Dictionary<string, object?> { ["isLoading"] = false, ["url"] = e.Request?.AbsoluteUri });

    private void OnNewWindowRequested(nint pointer)
    {
        using var args = new NativeComObject<INewWindowArgs>(pointer);
        args.Value.SetHandled(1);
        args.Value.GetUri(out var url);
        args.Value.GetIsUserInitiated(out var isUserInitiated);
        if (WebViewBrowserHost.IsAllowedUrl(url))
        {
            _host.RaiseNewTabRequested(this, url, isUserInitiated != 0);
        }
    }

    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs e)
    {
        if (!_disposed)
        {
            Raise("Host.closed", new Dictionary<string, object?> { ["reason"] = "The native browser adapter was destroyed." });
            DisposeNative();
        }
    }

    private void OnProtocolEvent(string method, nint argsPointer)
    {
        if (_disposed)
        {
            return;
        }
        using var args = new NativeComObject<ICdpEventArgs>(argsPointer);
        args.Value.GetParameterObjectAsJson(out var json);
        string? sessionId = null;
        if (args.Value is ICdpEventArgs2 extended)
        {
            extended.GetSessionId(out sessionId);
            if (sessionId?.Length == 0)
            {
                sessionId = null;
            }
        }
        using var document = JsonDocument.Parse(json);
        Raise(new BrowserProtocolEvent(method, document.RootElement.Clone(), sessionId));
    }

    private void OnScriptDialog(nint pointer)
    {
        CompleteDialog(false, null);
        _dialog = new NativeComObject<IScriptDialogArgs>(pointer);
        _dialog.Value.GetDeferral(out var deferralPointer);
        _dialogDeferral = new NativeComObject<IWebViewDeferral>(deferralPointer, true);
        _dialog.Value.GetUri(out var uri);
        _dialog.Value.GetMessage(out var message);
        _dialog.Value.GetDefaultText(out var prompt);
        _dialog.Value.GetKind(out var kind);
        Raise("Page.javascriptDialogOpening", new Dictionary<string, object?>
        {
            ["url"] = uri, ["message"] = message, ["defaultPrompt"] = prompt,
            ["type"] = kind switch { 0 => "alert", 1 => "confirm", 2 => "prompt", _ => "beforeunload" },
            ["hasBrowserHandler"] = true
        });
        _dialogTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _dialogTimer.Tick += OnDialogTimeout;
        _dialogTimer.Start();
    }

    private void OnDialogTimeout(object? sender, EventArgs e) => CompleteDialog(false, null);

    private void CompleteDialog(bool accept, string? prompt)
    {
        _dialogTimer?.Stop();
        _dialogTimer = null;
        var dialog = _dialog;
        var deferral = _dialogDeferral;
        _dialog = null;
        _dialogDeferral = null;
        if (dialog is null)
        {
            return;
        }
        try
        {
            if (accept)
            {
                if (prompt is not null)
                {
                    dialog.Value.SetResultText(prompt);
                }
                dialog.Value.Accept();
            }
            deferral?.Value.Complete();
            Raise("Page.javascriptDialogClosed", new Dictionary<string, object?> { ["result"] = accept, ["userInput"] = prompt ?? "" });
        }
        finally
        {
            deferral?.Dispose();
            dialog.Dispose();
        }
    }

    private void ConfigureDownloads(JsonElement parameters)
    {
        var behavior = parameters.TryGetProperty("behavior", out var value) ? value.GetString() : "default";
        if (behavior == "deny")
        {
            _downloadDirectory = null;
            return;
        }
        if (behavior is not ("default" or "allow" or "allowAndName"))
        {
            throw new ArgumentException("Unknown download behavior.");
        }
        var path = parameters.TryGetProperty("downloadPath", out var requested) && requested.GetString() is { Length: > 0 } supplied
            ? Path.GetFullPath(supplied) : _downloadRoot;
        if (!string.Equals(path, _downloadRoot, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(_downloadRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Downloads must remain within this browser tab's managed download directory.");
        }
        Directory.CreateDirectory(path);
        _downloadDirectory = path;
    }

    private void OnDownload(nint pointer)
    {
        using var args = new NativeComObject<IDownloadStartingArgs>(pointer);
        args.Value.SetHandled(1);
        args.Value.GetDownloadOperation(out var operationPointer);
        var operation = new NativeComObject<IDownloadOperation>(operationPointer, true);
        operation.Value.GetUri(out var uri);
        operation.Value.GetResultFilePath(out var suggestedPath);
        var guid = Guid.NewGuid().ToString("N");
        var filename = Path.GetFileName(suggestedPath);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            filename = filename.Replace(invalid, '_');
        }
        filename = filename.TrimEnd('.', ' ');
        if (filename.Length > 100)
        {
            var extension = Path.GetExtension(filename);
            extension = extension.Length <= 20 ? extension : "";
            filename = filename[..(100 - extension.Length)] + extension;
        }
        if (filename.Length == 0)
        {
            filename = "download";
        }
        Raise("Browser.downloadWillBegin", new Dictionary<string, object?>
        {
            ["guid"] = guid, ["url"] = uri, ["suggestedFilename"] = filename, ["frameId"] = ""
        });
        if (_disposed || _downloadDirectory is null || !WebViewBrowserHost.IsAllowedUrl(uri))
        {
            args.Value.SetCancel(1);
            operation.Dispose();
            Raise("Browser.downloadProgress", new Dictionary<string, object?>
            {
                ["guid"] = guid, ["state"] = "canceled", ["totalBytes"] = 0, ["receivedBytes"] = 0
            });
            return;
        }
        try
        {
            Directory.CreateDirectory(_downloadDirectory);
            var path = Path.Combine(_downloadDirectory, guid + "-" + filename);
            args.Value.SetResultFilePath(path);
            var download = new NativeDownload(this, guid, path, operation);
            _downloads.Add(guid, download);
            download.Start();
        }
        catch
        {
            args.Value.SetCancel(1);
            operation.Dispose();
            throw;
        }
    }

    private void OnDownloadProgress(NativeDownload download)
    {
        if (_disposed || !_downloads.ContainsKey(download.Id))
        {
            return;
        }
        download.Operation.Value.GetState(out var state);
        download.Operation.Value.GetTotalBytesToReceive(out var total);
        download.Operation.Value.GetBytesReceived(out var received);
        Raise("Browser.downloadProgress", new Dictionary<string, object?>
        {
            ["guid"] = download.Id, ["state"] = state switch { 0 => "inProgress", 1 => "canceled", _ => "completed" },
            ["totalBytes"] = total, ["receivedBytes"] = received,
            ["filePath"] = state == 2 ? download.Path : null
        });
        if (state != 0)
        {
            _downloads.Remove(download.Id);
            Dispatcher.UIThread.Post(download.Dispose);
        }
    }

    private void OnExternalUri(nint pointer)
    {
        using var args = new NativeComObject<IExternalUriArgs>(pointer);
        args.Value.SetCancel(1);
        args.Value.GetUri(out var uri);
        Raise("Host.navigationBlocked", new Dictionary<string, object?> { ["url"] = uri });
    }

    private void OnPermission(nint pointer)
    {
        using var args = new NativeComObject<IPermissionArgs>(pointer);
        args.Value.SetState(2);
        args.Value.GetUri(out var uri);
        args.Value.GetPermissionKind(out var kind);
        Raise("Host.permissionDenied", new Dictionary<string, object?> { ["url"] = uri, ["kind"] = kind });
    }

    private void OnFrameNavigation(nint pointer)
    {
        using var args = new NativeComObject<INavigationStartingArgs>(pointer);
        args.Value.GetUri(out var uri);
        if (!WebViewBrowserHost.IsAllowedUrl(uri))
        {
            args.Value.SetCancel(1);
        }
    }

    private void OnProcessFailed(nint pointer)
    {
        using var args = new NativeComObject<IProcessFailedArgs>(pointer);
        args.Value.GetProcessFailedKind(out var kind);
        Raise("Inspector.targetCrashed", new Dictionary<string, object?> { ["kind"] = kind });
        Dispatcher.UIThread.Post(Dispose);
    }

    private void OnTitleChanged()
    {
        _native!.Value.GetDocumentTitle(out var title);
        _native.Value.GetSource(out var url);
        Raise("Host.titleChanged", new Dictionary<string, object?> { ["title"] = title, ["url"] = url });
    }

    private void Raise(string method, object parameters) => Raise(new BrowserProtocolEvent(method, BrowserJson.Element(parameters)));

    private void Raise(BrowserProtocolEvent protocolEvent)
    {
        var handlers = ProtocolEvent;
        if (handlers is null)
        {
            return;
        }
        foreach (EventHandler<BrowserProtocolEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, protocolEvent);
            }
            catch (Exception)
            {
                // Managed subscriber failures cannot unwind through a native callback.
            }
        }
    }

    public void Dispose() => _host.DisposePage(this);

    internal void DisposeNative()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
        {
            return;
        }
        Raise("Host.closed", new Dictionary<string, object?> { ["reason"] = "The browser tab was closed." });
        _disposed = true;
        Dispatcher.UIThread.UnhandledException -= OnInitializationException;
        _ready.TrySetException(new ObjectDisposedException(nameof(WebViewBrowserPage)));
        CompleteDialog(false, null);
        foreach (var callback in _pending.Values)
        {
            callback.Completion.TrySetException(new ObjectDisposedException(nameof(WebViewBrowserPage)));
        }
        _pending.Clear();
        foreach (var download in _downloads.Values.ToArray())
        {
            TryNative(download.Operation.Value.Cancel);
            download.Dispose();
        }
        _downloads.Clear();
        foreach (var (receiver, token, _) in _receivers)
        {
            TryNative(() => receiver.Value.RemoveDevToolsProtocolEventReceived(token));
            receiver.Dispose();
        }
        _receivers.Clear();
        foreach (var remove in _unregister)
        {
            TryNative(remove);
        }
        _unregister.Clear();
        _native?.Dispose();
        _native = null;
        WebView.EnvironmentRequested -= OnEnvironmentRequested;
        if (WebView.TryGetPlatformHandle() is not null)
        {
            WebView.AdapterCreated -= OnAdapterCreated;
        }
        WebView.AdapterDestroyed -= OnAdapterDestroyed;
        WebView.NavigationStarted -= OnNavigationStarted;
        WebView.NavigationCompleted -= OnNavigationCompleted;
        // The HWND adapter is not the handle passed to Avalonia's destruction hook.
        if (WebView.TryGetPlatformHandle() is IDisposable adapter)
        {
            TryNative(adapter.Dispose);
        }
        ProtocolEvent = null;
    }

    private static void TryNative(Action action)
    {
        try { action(); }
        catch (COMException) { }
    }

    private sealed class NativeDownload : IDisposable
    {
        private readonly DownloadCallback _callback;
        private long _bytesToken;
        private long _stateToken;
        private bool _disposed;

        public NativeDownload(WebViewBrowserPage page, string id, string path, NativeComObject<IDownloadOperation> operation)
        {
            Id = id;
            Path = path;
            Operation = operation;
            _callback = new DownloadCallback(page, this);
        }

        public string Id { get; }
        public string Path { get; }
        public NativeComObject<IDownloadOperation> Operation { get; }

        public void Start()
        {
            Operation.Value.AddBytesReceivedChanged(_callback, out _bytesToken);
            Operation.Value.AddStateChanged(_callback, out _stateToken);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            TryNative(() => Operation.Value.RemoveBytesReceivedChanged(_bytesToken));
            TryNative(() => Operation.Value.RemoveStateChanged(_stateToken));
            Operation.Dispose();
        }
    }

    [GeneratedComClass]
    private sealed partial class DownloadCallback(WebViewBrowserPage page, NativeDownload download)
        : IDownloadBytesHandler, IDownloadStateHandler
    {
        public int Invoke(nint sender, nint args)
        {
            try { page.OnDownloadProgress(download); }
            catch (Exception) { }
            return 0;
        }
    }

    [GeneratedComClass]
    private sealed partial class CdpCompletedCallback(WebViewBrowserPage page, long id) : ICdpCompletedHandler
    {
        public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Invoke(int errorCode, string? json)
        {
            page._pending.Remove(id);
            try
            {
                if (errorCode < 0)
                {
                    Completion.TrySetException(new COMException("The WebView2 CDP command failed.", errorCode));
                }
                else
                {
                    using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    Completion.TrySetResult(document.RootElement.Clone());
                }
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
            return 0;
        }
    }

    [GeneratedComClass]
    private sealed partial class CdpEventCallback(WebViewBrowserPage page, string method) : ICdpEventHandler
    {
        public int Invoke(nint sender, nint args)
        {
            try { page.OnProtocolEvent(method, args); }
            catch (Exception) { }
            return 0;
        }
    }

    [GeneratedComClass]
    private sealed partial class NativeCallbacks(WebViewBrowserPage page) : IScriptDialogHandler, IDownloadStartingHandler,
        IExternalUriHandler, IPermissionHandler, IProcessFailedHandler, ISimpleWebViewHandler,
        IWindowCloseHandler, INavigationStartingHandler, INewWindowHandler
    {
        int INewWindowHandler.Invoke(nint sender, nint args) => Invoke(() => page.OnNewWindowRequested(args));
        int IScriptDialogHandler.Invoke(nint sender, nint args) => Invoke(() => page.OnScriptDialog(args));
        int IDownloadStartingHandler.Invoke(nint sender, nint args) => Invoke(() => page.OnDownload(args));
        int IExternalUriHandler.Invoke(nint sender, nint args) => Invoke(() => page.OnExternalUri(args));
        int IPermissionHandler.Invoke(nint sender, nint args) => Invoke(() => page.OnPermission(args));
        int IProcessFailedHandler.Invoke(nint sender, nint args) => Invoke(() => page.OnProcessFailed(args));
        int ISimpleWebViewHandler.Invoke(nint sender, nint args) => Invoke(page.OnTitleChanged);
        int IWindowCloseHandler.Invoke(nint sender, nint args) => Invoke(() =>
        {
            page.Raise("Host.closed", new Dictionary<string, object?>());
            Dispatcher.UIThread.Post(page.Dispose);
        });
        int INavigationStartingHandler.Invoke(nint sender, nint args) => Invoke(() => page.OnFrameNavigation(args));

        private static int Invoke(Action action)
        {
            try { action(); return 0; }
            catch (Exception ex) { return ex.HResult; }
        }
    }
}
