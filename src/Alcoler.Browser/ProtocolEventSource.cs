using System.Collections.Frozen;

namespace Alcoler.Browser;

// Protocol-event fan-out shared by the IBrowserPage backends. Handlers run from a
// snapshot taken under the gate but invoked outside it, so a subscriber may mutate
// the handler list while an event is still being delivered.
public sealed class ProtocolEventSource
{
    // Methods any ProtocolEvent subscriber acts on: the union of
    // CdpBrowserAutomation.OnProtocolEvent and BrowserController.OnProtocolEvent.
    // Everything else (DOM mutation storms, console output, targetInfo churn) is
    // never parsed; a test pins that DOM mutations stay unparsed.
    public static readonly FrozenSet<string> ConsumedProtocolMethods = new string[]
    {
        "Page.frameNavigated", "Page.navigatedWithinDocument", "DOM.documentUpdated",
        "Runtime.executionContextsCleared", "Runtime.executionContextDestroyed", "Page.frameDetached", "Target.attachedToTarget",
        "Target.detachedFromTarget",
        "Page.lifecycleEvent", "Host.titleChanged", "Host.loadingChanged",
        "Page.javascriptDialogOpening", "Page.javascriptDialogClosed",
        "Browser.downloadWillBegin", "Page.downloadWillBegin",
        "Browser.downloadProgress", "Page.downloadProgress",
        "Page.frameStartedLoading", "Page.frameStoppedLoading",
        "Host.closed", "Inspector.detached", "Inspector.targetCrashed"
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly object _gate = new();
    private EventHandler<BrowserProtocolEvent>? _event;
    private EventHandler<BrowserProtocolEvent>[]? _handlers;

    // Lock-free early-out only; Raise re-checks under the gate.
    public bool IsSubscribed => _event is not null;

    public void Add(EventHandler<BrowserProtocolEvent>? handler)
    {
        lock (_gate)
        {
            _event += handler;
            _handlers = null;
        }
    }

    public void Remove(EventHandler<BrowserProtocolEvent>? handler)
    {
        lock (_gate)
        {
            _event -= handler;
            _handlers = null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _event = null;
            _handlers = null;
        }
    }

    public void Raise(object sender, BrowserProtocolEvent protocolEvent)
    {
        EventHandler<BrowserProtocolEvent>[] handlers;
        lock (_gate)
        {
            if (_event is null) return;
            if (_handlers is null)
            {
                var delegates = _event.GetInvocationList();
                var rebuilt = new EventHandler<BrowserProtocolEvent>[delegates.Length];
                for (var index = 0; index < delegates.Length; index++)
                {
                    rebuilt[index] = (EventHandler<BrowserProtocolEvent>)delegates[index];
                }
                _handlers = rebuilt;
            }
            handlers = _handlers;
        }
        foreach (var handler in handlers)
        {
            try
            {
                handler(sender, protocolEvent);
            }
            catch (Exception)
            {
                // Managed subscriber failures cannot unwind the native event source.
            }
        }
    }
}
