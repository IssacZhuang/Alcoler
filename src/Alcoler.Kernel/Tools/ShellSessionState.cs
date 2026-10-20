using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

public sealed class ShellSessionState(string initialDirectory) : IShellSessionState
{
    private readonly object _sync = new();
    private string _currentDirectory = initialDirectory;

    public string CurrentDirectory
    {
        get
        {
            lock (_sync)
            {
                return _currentDirectory;
            }
        }
    }

    public void SetCurrentDirectory(string path)
    {
        lock (_sync)
        {
            _currentDirectory = path;
        }
    }
}
