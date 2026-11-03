using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Alcoler.Adapters.Tools;

namespace Alcoler.Adapters.Grep;

internal sealed record AlcoGrepQuery(
    string Pattern,
    string Path,
    IReadOnlyList<string> Globs,
    uint Mode,
    bool CaseInsensitive,
    bool Multiline,
    bool OnlyMatching,
    uint BeforeContext,
    uint AfterContext);

/// <summary>
/// Facade over the alco-grep native library: probes availability once (a
/// missing or ABI-incompatible library is unavailable, never fatal — the
/// managed engine takes over), marshals one search call, and maps status
/// codes to exceptions.
/// </summary>
internal static unsafe class AlcoGrepNative
{
    private static readonly Lazy<bool> Available = new(Probe);

    public static bool IsAvailable => Available.Value;

    private static bool Probe()
    {
        try
        {
            AlcoGrepNativeLibrary.EnsureLoaded();
            return AlcoGrepRaw.GrepAbiVersion() >> 16 == AlcoGrepAbi.AbiMajor;
        }
        catch
        {
            return false;
        }
    }

    public static void Search(AlcoGrepQuery query, GrepResultCollector result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var receiver = new EventReceiver(result);
        var receiverHandle = GCHandle.Alloc(receiver);
        var cancel = new PinnedFlag();
        var pattern = new PinnedUtf8(query.Pattern);
        var path = new PinnedUtf8(query.Path);
        var globs = query.Globs.Select(glob => new PinnedUtf8(glob)).ToList();
        var pointers = new PinnedPointers(globs.Count);
        try
        {
            for (var index = 0; index < globs.Count; index++)
                pointers.Set(index, globs[index].Pointer);
            var options = new AlcoGrepSearchOptions
            {
                Pattern = pattern.Pointer,
                Path = path.Pointer,
                Globs = pointers.Pointer,
                GlobCount = (uint)globs.Count,
                Mode = query.Mode,
                CaseInsensitive = query.CaseInsensitive ? 1u : 0u,
                Multiline = query.Multiline ? 1u : 0u,
                OnlyMatching = query.OnlyMatching ? 1u : 0u,
                BeforeContext = query.BeforeContext,
                AfterContext = query.AfterContext,
            };
            using var registration = cancellationToken.Register(
                static state => RaiseCancel((nint)state!),
                (nint)cancel.Pointer);
            var status = AlcoGrepRaw.GrepSearch(in options, &OnEvent, (void*)GCHandle.ToIntPtr(receiverHandle), cancel.Pointer);
            receiver.Failure?.Throw();
            switch (status)
            {
                case AlcoGrepAbi.StatusOk:
                    return;
                case AlcoGrepAbi.StatusCancelled:
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException("alco-grep search was cancelled", cancellationToken);
                case AlcoGrepAbi.StatusInvalidPattern:
                    throw new GrepPatternException(query.Pattern);
                default:
                    throw new InvalidOperationException($"alco-grep search failed ({DescribeError(status)}).");
            }
        }
        finally
        {
            foreach (var glob in globs)
                glob.Dispose();
            pointers.Dispose();
            pattern.Dispose();
            path.Dispose();
            cancel.Dispose();
            receiverHandle.Free();
        }
    }

    private static void RaiseCancel(nint flagPointer) => *(uint*)flagPointer = 1;

    private static string DescribeError(uint status)
    {
        var info = new AlcoGrepErrorInfo();
        AlcoGrepRaw.GrepLastError(ref info);
        var message = info.Message is null ? null : Marshal.PtrToStringUTF8((nint)info.Message);
        return $"status {status}: {message ?? "unknown alco-grep failure"}";
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint OnEvent(AlcoGrepMatchEvent* ffiEvent, void* userdata)
    {
        if (GCHandle.FromIntPtr((nint)userdata).Target is not EventReceiver receiver)
            return 1;
        return receiver.Add(ffiEvent) ? 0u : 1u;
    }

    private sealed class EventReceiver(GrepResultCollector result)
    {
        private readonly object _gate = new();

        public ExceptionDispatchInfo? Failure { get; private set; }

        // Native callbacks may overlap across files and must never unwind through the ABI.
        public bool Add(AlcoGrepMatchEvent* ffiEvent)
        {
            lock (_gate)
            {
                if (Failure is not null)
                    return false;
                try
                {
                    if (ffiEvent->Kind == AlcoGrepAbi.EventFileCount)
                    {
                        result.AddFile(Marshal.PtrToStringUTF8((nint)ffiEvent->Path) ?? "", (long)ffiEvent->Count);
                        return true;
                    }
                    if (ffiEvent->Kind is not (AlcoGrepAbi.EventMatch or AlcoGrepAbi.EventContext))
                        return true;
                    var pathLength = 0;
                    if (ffiEvent->Path is not null)
                        while (ffiEvent->Path[pathLength] != 0)
                            pathLength++;
                    char[]? rented = null;
                    Span<char> path = pathLength <= 1024
                        ? stackalloc char[pathLength]
                        : rented = ArrayPool<char>.Shared.Rent(pathLength);
                    try
                    {
                        var count = Encoding.UTF8.GetChars(new ReadOnlySpan<byte>(ffiEvent->Path, pathLength), path);
                        var lineNumber = ffiEvent->Kind == AlcoGrepAbi.EventMatch && ffiEvent->LineNumber == 0
                            ? (int?)null : (int)ffiEvent->LineNumber;
                        result.AddContent(path[..count], lineNumber, ffiEvent->Text);
                    }
                    finally
                    {
                        if (rented is not null)
                            ArrayPool<char>.Shared.Return(rented);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Failure = ExceptionDispatchInfo.Capture(ex);
                    return false;
                }
            }
        }
    }

    private sealed class PinnedUtf8 : IDisposable
    {
        private readonly GCHandle _handle;

        public PinnedUtf8(string value)
        {
            var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
            Encoding.UTF8.GetBytes(value, bytes);
            _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            Pointer = (byte*)_handle.AddrOfPinnedObject();
        }

        public byte* Pointer { get; }

        public void Dispose() => _handle.Free();
    }

    private sealed class PinnedPointers : IDisposable
    {
        private readonly nint[] _values;
        private GCHandle _handle;

        public PinnedPointers(int count)
        {
            _values = new nint[count];
            _handle = GCHandle.Alloc(_values, GCHandleType.Pinned);
        }

        public byte** Pointer => (byte**)_handle.AddrOfPinnedObject();

        public void Set(int index, byte* value) => _values[index] = (nint)value;

        public void Dispose()
        {
            if (_handle.IsAllocated)
                _handle.Free();
        }
    }

    private sealed class PinnedFlag : IDisposable
    {
        private readonly GCHandle _handle;

        public PinnedFlag()
        {
            _handle = GCHandle.Alloc(new uint[1], GCHandleType.Pinned);
            Pointer = (uint*)_handle.AddrOfPinnedObject();
        }

        public uint* Pointer { get; }

        public void Dispose() => _handle.Free();
    }
}
