using Alcoler.Logging;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Alcoler.Browser;

internal sealed class WebViewBrowserControl : NativeWebView
{
    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        if (!TryRestoreFocus(e))
        {
            // Modal owner activation can precede native re-enablement.
            Dispatcher.UIThread.Post(() =>
            {
                if (Focusable && IsFocused && IsEffectivelyEnabled && IsEffectivelyVisible
                    && TopLevel.GetTopLevel(this) is Window { IsActive: true })
                {
                    TryRestoreFocus(e);
                }
            }, DispatcherPriority.Input);
        }
    }

    private bool TryRestoreFocus(GotFocusEventArgs e)
    {
        try
        {
            base.OnGotFocus(e);
            return true;
        }
        catch (ArgumentException exception) when (exception.HResult == unchecked((int)0x80070057))
        {
            AppLog.CreateLogger("Alcoler.Browser.WebViewBrowserControl")
                .LogWarning(exception, "WebView2 rejected keyboard focus.");
            return false;
        }
    }
}
