using System.Runtime.InteropServices;

namespace Alcoler.Adapters.Grep;

/// <summary>
/// Raw P/Invoke surface of the alco-grep native library. Fallible entries
/// return a status from <see cref="AlcoGrepAbi"/>; failures are surfaced by
/// the <see cref="AlcoGrepNative"/> facade, never from inside callbacks.
/// </summary>
internal static unsafe partial class AlcoGrepRaw
{
    private const string LibraryName = "alco_grep";

    [LibraryImport(LibraryName, EntryPoint = "grep_abi_version")]
    public static partial uint GrepAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "grep_search")]
    public static partial uint GrepSearch(
        in AlcoGrepSearchOptions options,
        delegate* unmanaged[Cdecl]<AlcoGrepMatchEvent*, void*, uint> callback,
        void* userdata,
        uint* cancelFlag);

    [LibraryImport(LibraryName, EntryPoint = "grep_last_error")]
    public static partial void GrepLastError(ref AlcoGrepErrorInfo info);
}
