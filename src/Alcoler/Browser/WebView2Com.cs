using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Alcoler.Browser;

// Method order and IIDs follow Microsoft's WebView2.idl, including all inherited slots.
[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("76eceacb-0462-4d94-ac83-423a6793775e")]
internal partial interface ICoreWebView2
{
    void GetSettings(out nint value);
    void GetSource([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void Navigate([MarshalAs(UnmanagedType.LPWStr)] string uri);
    void NavigateToString([MarshalAs(UnmanagedType.LPWStr)] string html);
    void AddNavigationStarting(nint handler, out long token);
    void RemoveNavigationStarting(long token);
    void AddContentLoading(nint handler, out long token);
    void RemoveContentLoading(long token);
    void AddSourceChanged(nint handler, out long token);
    void RemoveSourceChanged(long token);
    void AddHistoryChanged(nint handler, out long token);
    void RemoveHistoryChanged(long token);
    void AddNavigationCompleted(nint handler, out long token);
    void RemoveNavigationCompleted(long token);
    void AddFrameNavigationStarting(INavigationStartingHandler handler, out long token);
    void RemoveFrameNavigationStarting(long token);
    void AddFrameNavigationCompleted(nint handler, out long token);
    void RemoveFrameNavigationCompleted(long token);
    void AddScriptDialogOpening(IScriptDialogHandler handler, out long token);
    void RemoveScriptDialogOpening(long token);
    void AddPermissionRequested(IPermissionHandler handler, out long token);
    void RemovePermissionRequested(long token);
    void AddProcessFailed(IProcessFailedHandler handler, out long token);
    void RemoveProcessFailed(long token);
    void AddScriptToExecuteOnDocumentCreated([MarshalAs(UnmanagedType.LPWStr)] string script, nint handler);
    void RemoveScriptToExecuteOnDocumentCreated([MarshalAs(UnmanagedType.LPWStr)] string id);
    void ExecuteScript([MarshalAs(UnmanagedType.LPWStr)] string script, nint handler);
    void CapturePreview(int format, nint stream, nint handler);
    void Reload();
    void PostWebMessageAsJson([MarshalAs(UnmanagedType.LPWStr)] string json);
    void PostWebMessageAsString([MarshalAs(UnmanagedType.LPWStr)] string value);
    void AddWebMessageReceived(nint handler, out long token);
    void RemoveWebMessageReceived(long token);
    void CallDevToolsProtocolMethod([MarshalAs(UnmanagedType.LPWStr)] string method,
        [MarshalAs(UnmanagedType.LPWStr)] string parameters, ICdpCompletedHandler handler);
    void GetBrowserProcessId(out uint value);
    void GetCanGoBack(out int value);
    void GetCanGoForward(out int value);
    void GoBack();
    void GoForward();
    void GetDevToolsProtocolEventReceiver([MarshalAs(UnmanagedType.LPWStr)] string name, out nint receiver);
    void Stop();
    void AddNewWindowRequested(INewWindowHandler handler, out long token);
    void RemoveNewWindowRequested(long token);
    void AddDocumentTitleChanged(ISimpleWebViewHandler handler, out long token);
    void RemoveDocumentTitleChanged(long token);
    void GetDocumentTitle([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void AddHostObjectToScript([MarshalAs(UnmanagedType.LPWStr)] string name, nint value);
    void RemoveHostObjectFromScript([MarshalAs(UnmanagedType.LPWStr)] string name);
    void OpenDevToolsWindow();
    void AddContainsFullScreenElementChanged(nint handler, out long token);
    void RemoveContainsFullScreenElementChanged(long token);
    void GetContainsFullScreenElement(out int value);
    void AddWebResourceRequested(nint handler, out long token);
    void RemoveWebResourceRequested(long token);
    void AddWebResourceRequestedFilter([MarshalAs(UnmanagedType.LPWStr)] string uri, int context);
    void RemoveWebResourceRequestedFilter([MarshalAs(UnmanagedType.LPWStr)] string uri, int context);
    void AddWindowCloseRequested(IWindowCloseHandler handler, out long token);
    void RemoveWindowCloseRequested(long token);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("9e8f0cf8-e670-4b5e-b2bc-73e061e3184c")]
internal partial interface ICoreWebView2_2 : ICoreWebView2
{
    void AddWebResourceResponseReceived(nint handler, out long token);
    void RemoveWebResourceResponseReceived(long token);
    void NavigateWithWebResourceRequest(nint request);
    void AddDomContentLoaded(nint handler, out long token);
    void RemoveDomContentLoaded(long token);
    void GetCookieManager(out nint value);
    void GetEnvironment(out nint value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("a0d6df20-3b92-416d-aa0c-437a9c727857")]
internal partial interface ICoreWebView2_3 : ICoreWebView2_2
{
    void TrySuspend(nint handler);
    void Resume();
    void GetIsSuspended(out int value);
    void SetVirtualHostNameToFolderMapping([MarshalAs(UnmanagedType.LPWStr)] string host,
        [MarshalAs(UnmanagedType.LPWStr)] string folder, int access);
    void ClearVirtualHostNameToFolderMapping([MarshalAs(UnmanagedType.LPWStr)] string host);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("20d02d59-6df2-42dc-bd06-f98a694b1302")]
internal partial interface ICoreWebView2_4 : ICoreWebView2_3
{
    void AddFrameCreated(nint handler, out long token);
    void RemoveFrameCreated(long token);
    void AddDownloadStarting(IDownloadStartingHandler handler, out long token);
    void RemoveDownloadStarting(long token);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("bedb11b8-d63c-11eb-b8bc-0242ac130003")]
internal partial interface ICoreWebView2_5 : ICoreWebView2_4
{
    void AddClientCertificateRequested(nint handler, out long token);
    void RemoveClientCertificateRequested(long token);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("499aadac-d92c-4589-8a75-111bfc167795")]
internal partial interface ICoreWebView2_6 : ICoreWebView2_5
{
    void OpenTaskManagerWindow();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("79c24d83-09a3-45ae-9418-487f32a58740")]
internal partial interface ICoreWebView2_7 : ICoreWebView2_6
{
    void PrintToPdf([MarshalAs(UnmanagedType.LPWStr)] string path, nint settings, nint handler);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("e9632730-6e1e-43ab-b7b8-7b2c9e62e094")]
internal partial interface ICoreWebView2_8 : ICoreWebView2_7
{
    void AddIsMutedChanged(nint handler, out long token);
    void RemoveIsMutedChanged(long token);
    void GetIsMuted(out int value);
    void SetIsMuted(int value);
    void AddIsDocumentPlayingAudioChanged(nint handler, out long token);
    void RemoveIsDocumentPlayingAudioChanged(long token);
    void GetIsDocumentPlayingAudio(out int value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("4d7b2eab-9fdc-468d-b998-a9260b5ed651")]
internal partial interface ICoreWebView2_9 : ICoreWebView2_8
{
    void AddIsDefaultDownloadDialogOpenChanged(nint handler, out long token);
    void RemoveIsDefaultDownloadDialogOpenChanged(long token);
    void GetIsDefaultDownloadDialogOpen(out int value);
    void OpenDefaultDownloadDialog();
    void CloseDefaultDownloadDialog();
    void GetDefaultDownloadDialogCornerAlignment(out int value);
    void SetDefaultDownloadDialogCornerAlignment(int value);
    void GetDefaultDownloadDialogMargin(out NativePoint value);
    void SetDefaultDownloadDialogMargin(NativePoint value);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("b1690564-6f5a-4983-8e48-31d1143fecdb")]
internal partial interface ICoreWebView2_10 : ICoreWebView2_9
{
    void AddBasicAuthenticationRequested(nint handler, out long token);
    void RemoveBasicAuthenticationRequested(long token);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("0be78e56-c193-4051-b943-23b460c08bdb")]
internal partial interface ICoreWebView2_11 : ICoreWebView2_10
{
    void CallDevToolsProtocolMethodForSession([MarshalAs(UnmanagedType.LPWStr)] string session,
        [MarshalAs(UnmanagedType.LPWStr)] string method, [MarshalAs(UnmanagedType.LPWStr)] string parameters,
        ICdpCompletedHandler handler);
    void AddContextMenuRequested(nint handler, out long token);
    void RemoveContextMenuRequested(long token);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("35d69927-bcfa-4566-9349-6b3e0d154cac")]
internal partial interface ICoreWebView2_12 : ICoreWebView2_11
{
    void AddStatusBarTextChanged(nint handler, out long token);
    void RemoveStatusBarTextChanged(long token);
    void GetStatusBarText([MarshalAs(UnmanagedType.LPWStr)] out string value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("f75f09a8-667e-4983-88d6-c8773f315e84")]
internal partial interface ICoreWebView2_13 : ICoreWebView2_12
{
    void GetProfile(out nint value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("6daa4f10-4a90-4753-8898-77c5df534165")]
internal partial interface ICoreWebView2_14 : ICoreWebView2_13
{
    void AddServerCertificateErrorDetected(nint handler, out long token);
    void RemoveServerCertificateErrorDetected(long token);
    void ClearServerCertificateErrorActions(nint handler);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("517b2d1d-7dae-4a66-a4f4-10352ffb9518")]
internal partial interface ICoreWebView2_15 : ICoreWebView2_14
{
    void AddFaviconChanged(nint handler, out long token);
    void RemoveFaviconChanged(long token);
    void GetFaviconUri([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetFavicon(int format, nint handler);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("0eb34dc9-9f91-41e1-8639-95cd5943906b")]
internal partial interface ICoreWebView2_16 : ICoreWebView2_15
{
    void Print(nint settings, nint handler);
    void ShowPrintUi(int kind);
    void PrintToPdfStream(nint settings, nint handler);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("702e75d4-fd44-434d-9d70-1a68a6b1192a")]
internal partial interface ICoreWebView2_17 : ICoreWebView2_16
{
    void PostSharedBufferToScript(nint buffer, int access, [MarshalAs(UnmanagedType.LPWStr)] string json);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("7a626017-28be-49b2-b865-3ba2b3522d90")]
internal partial interface ICoreWebView2_18 : ICoreWebView2_17
{
    void AddLaunchingExternalUriScheme(IExternalUriHandler handler, out long token);
    void RemoveLaunchingExternalUriScheme(long token);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("e562e4f0-d7fa-43ac-8d71-c05150499f00")]
internal partial interface IWebViewSettings
{
    void GetIsScriptEnabled(out int value);
    void SetIsScriptEnabled(int value);
    void GetIsWebMessageEnabled(out int value);
    void SetIsWebMessageEnabled(int value);
    void GetAreDefaultScriptDialogsEnabled(out int value);
    void SetAreDefaultScriptDialogsEnabled(int value);
    void GetIsStatusBarEnabled(out int value);
    void SetIsStatusBarEnabled(int value);
    void GetAreDevToolsEnabled(out int value);
    void SetAreDevToolsEnabled(int value);
    void GetAreDefaultContextMenusEnabled(out int value);
    void SetAreDefaultContextMenusEnabled(int value);
    void GetAreHostObjectsAllowed(out int value);
    void SetAreHostObjectsAllowed(int value);
    void GetIsZoomControlEnabled(out int value);
    void SetIsZoomControlEnabled(int value);
    void GetIsBuiltInErrorPageEnabled(out int value);
    void SetIsBuiltInErrorPageEnabled(int value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("b32ca51a-8371-45e9-9317-af021d080367")]
internal partial interface ICdpEventReceiver
{
    void AddDevToolsProtocolEventReceived(ICdpEventHandler handler, out long token);
    void RemoveDevToolsProtocolEventReceived(long token);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("653c2959-bb3a-4377-8632-b58ada4e66c4")]
internal partial interface ICdpEventArgs
{
    void GetParameterObjectAsJson([MarshalAs(UnmanagedType.LPWStr)] out string value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("2dc4959d-1494-4393-95ba-bea4cb9ebd1b")]
internal partial interface ICdpEventArgs2 : ICdpEventArgs
{
    void GetSessionId([MarshalAs(UnmanagedType.LPWStr)] out string value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("5c4889f0-5ef6-4c5a-952c-d8f1b92d0574")]
internal partial interface ICdpCompletedHandler
{
    [PreserveSig] int Invoke(int errorCode, [MarshalAs(UnmanagedType.LPWStr)] string? json);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("e2fda4be-5456-406c-a261-3d452138362c")]
internal partial interface ICdpEventHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("ef381bf9-afa8-4e37-91c4-8ac48524bdfb")]
internal partial interface IScriptDialogHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("efedc989-c396-41ca-83f7-07f845a55724")]
internal partial interface IDownloadStartingHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("74f712e0-8165-43a9-a13f-0cce597e75df")]
internal partial interface IExternalUriHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("15e1c6a3-c72a-4df3-91d7-d097fbec6bfd")]
internal partial interface IPermissionHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("79e0aea4-990b-42d9-aa1d-0fcc2e5bc7f1")]
internal partial interface IProcessFailedHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("f5f2b923-953e-4042-9f95-f3a118e1afd4")]
internal partial interface ISimpleWebViewHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("5c19e9e0-092f-486b-affa-ca8231913039")]
internal partial interface IWindowCloseHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("9adbe429-f36d-432b-9ddc-f8881fbd76e3")]
internal partial interface INavigationStartingHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("d4c185fe-c81c-4989-97af-2d3fa7ab5651")]
internal partial interface INewWindowHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("34acb11c-fc37-4418-9132-f9c21d1eafb9")]
internal partial interface INewWindowArgs
{
    void GetUri([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void SetNewWindow(nint value);
    void GetNewWindow(out nint value);
    void SetHandled(int value);
    void GetHandled(out int value);
    void GetIsUserInitiated(out int value);
    void GetDeferral(out nint value);
    void GetWindowFeatures(out nint value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("7390bb70-abe0-4843-9529-f143b31b03d6")]
internal partial interface IScriptDialogArgs
{
    void GetUri([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetKind(out int value);
    void GetMessage([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void Accept();
    void GetDefaultText([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetResultText([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void SetResultText([MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetDeferral(out nint value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("c10e7f7b-b585-46f0-a623-8befbf3e4ee0")]
internal partial interface IWebViewDeferral
{
    void Complete();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("e99bbe21-43e9-4544-a732-282764eafa60")]
internal partial interface IDownloadStartingArgs
{
    void GetDownloadOperation(out nint value);
    void GetCancel(out int value);
    void SetCancel(int value);
    void GetResultFilePath([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void SetResultFilePath([MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetHandled(out int value);
    void SetHandled(int value);
    void GetDeferral(out nint value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("07d1a6c3-7175-4ba1-9306-e593ca07e46c")]
internal partial interface IExternalUriArgs
{
    void GetUri([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetInitiatingOrigin([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetIsUserInitiated(out int value);
    void GetCancel(out int value);
    void SetCancel(int value);
    void GetDeferral(out nint value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("973ae2ef-ff18-4894-8fb2-3c758f046810")]
internal partial interface IPermissionArgs
{
    void GetUri([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetPermissionKind(out int value);
    void GetIsUserInitiated(out int value);
    void GetState(out int value);
    void SetState(int value);
    void GetDeferral(out nint value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("8155a9a4-1474-4a86-8cae-151b0fa6b8ca")]
internal partial interface IProcessFailedArgs
{
    void GetProcessFailedKind(out int value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("5b495469-e119-438a-9b18-7604f25f2e49")]
internal partial interface INavigationStartingArgs
{
    void GetUri([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetIsUserInitiated(out int value);
    void GetIsRedirected(out int value);
    void GetRequestHeaders(out nint value);
    void GetCancel(out int value);
    void SetCancel(int value);
    void GetNavigationId(out ulong value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("79110ad3-cd5d-4373-8bc3-c60658f17a5f")]
internal partial interface IWebViewProfile
{
    void GetProfileName([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetIsInPrivateModeEnabled(out int value);
    void GetProfilePath([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetDefaultDownloadFolderPath([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void SetDefaultDownloadFolderPath([MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetPreferredColorScheme(out int value);
    void SetPreferredColorScheme(int value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("3d6b6cf2-afe1-44c7-a995-c65117714336")]
internal partial interface IDownloadOperation
{
    void AddBytesReceivedChanged(IDownloadBytesHandler handler, out long token);
    void RemoveBytesReceivedChanged(long token);
    void AddEstimatedEndTimeChanged(nint handler, out long token);
    void RemoveEstimatedEndTimeChanged(long token);
    void AddStateChanged(IDownloadStateHandler handler, out long token);
    void RemoveStateChanged(long token);
    void GetUri([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetContentDisposition([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetMimeType([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetTotalBytesToReceive(out long value);
    void GetBytesReceived(out long value);
    void GetEstimatedEndTime([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetResultFilePath([MarshalAs(UnmanagedType.LPWStr)] out string value);
    void GetState(out int value);
    void GetInterruptReason(out int value);
    void Cancel();
    void Pause();
    void Resume();
    void GetCanResume(out int value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("828e8ab6-d94c-4264-9cef-5217170d6251")]
internal partial interface IDownloadBytesHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("81336594-7ede-4ba9-bf71-acf0a95b58dd")]
internal partial interface IDownloadStateHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

internal sealed class NativeComObject<T> : IDisposable where T : class
{
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private readonly ComObject _wrapper;

    public NativeComObject(nint pointer, bool ownsReference = false)
    {
        if (pointer == 0)
        {
            throw new InvalidOperationException($"WebView2 returned a null {typeof(T).Name} pointer.");
        }
        try
        {
            _wrapper = (ComObject)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            Value = (T)(object)_wrapper;
        }
        finally
        {
            if (ownsReference)
            {
                Marshal.Release(pointer);
            }
        }
    }

    public T Value { get; }

    public void Dispose() => _wrapper.FinalRelease();
}
