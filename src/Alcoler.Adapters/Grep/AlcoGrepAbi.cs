using System.Runtime.InteropServices;

namespace Alcoler.Adapters.Grep;

/// <summary>
/// Field-by-field mirror of the alco-grep C ABI (see
/// src/Alcoler.Adapters.Native/alco-grep/src/abi.rs). Booleans are u32,
/// arrays are (pointer, count) pairs, and strings are NUL-terminated UTF-8.
/// </summary>
internal static class AlcoGrepAbi
{
    public const uint AbiMajor = 1;
    public const uint AbiMinor = 0;

    public const uint StatusOk = 0;
    public const uint StatusInvalidArgument = 2;
    public const uint StatusPanic = 6;
    public const uint StatusInvalidPattern = 9;
    public const uint StatusCancelled = 10;

    public const uint ModeContent = 1;
    public const uint ModeFilesWithMatches = 2;
    public const uint ModeCount = 3;

    public const uint EventMatch = 1;
    public const uint EventContext = 2;
    public const uint EventFileCount = 3;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AlcoGrepSearchOptions
{
    public byte* Pattern;
    public byte* Path;
    public byte** Globs;
    public uint GlobCount;
    public uint Mode;
    public uint CaseInsensitive;
    public uint Multiline;
    public uint OnlyMatching;
    public uint BeforeContext;
    public uint AfterContext;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AlcoGrepMatchEvent
{
    public uint Kind;
    public byte* Path;
    public ulong LineNumber;
    public byte* Text;
    public ulong Count;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AlcoGrepErrorInfo
{
    public uint Status;
    public byte* Message;
}
