using System.Runtime.InteropServices;

namespace Alcoler.Adapters.Grep;

/// <summary>
/// Resolves the alco-grep library from the application directory before
/// default probing. A failed load stays retryable; the resolver is registered
/// only after a successful load.
/// </summary>
internal static class AlcoGrepNativeLibrary
{
    private static readonly Lock LoadLock = new();
    private static nint _library;
    private static bool _resolverRegistered;

    public static void EnsureLoaded()
    {
        lock (LoadLock)
        {
            if (_resolverRegistered)
            {
                return;
            }

            string fileName = OperatingSystem.IsWindows() ? "alco_grep.dll"
                : OperatingSystem.IsMacOS() ? "libalco_grep.dylib" : "libalco_grep.so";
            if (_library == 0 && !NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, fileName), out _library))
            {
                _library = NativeLibrary.Load("alco_grep", typeof(AlcoGrepNativeLibrary).Assembly, null);
            }

            NativeLibrary.SetDllImportResolver(typeof(AlcoGrepNativeLibrary).Assembly,
                static (name, assembly, searchPath) => name == "alco_grep" ? _library : 0);
            _resolverRegistered = true;
        }
    }
}
