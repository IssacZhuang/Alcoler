using System.Runtime;

namespace Alcoler;

internal static class GcMaintenance
{
    // CompactOnce auto-resets once the collection completes; this is a full blocking GC
    // and must never run on the UI thread.
    internal static void CompactLargeObjectHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
    }
}
