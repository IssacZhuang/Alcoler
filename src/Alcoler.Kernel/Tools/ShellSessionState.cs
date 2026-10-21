using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

public sealed class ShellSessionState(string initialDirectory, ShellEnvironment? environment = null) : IShellSessionState
{
    private readonly object _sync = new();
    private string _currentDirectory = initialDirectory;
    private ShellEnvironment? _environment = environment;

    public ShellEnvironment? Environment => Volatile.Read(ref _environment);

    internal void SetEnvironment(ShellEnvironment environment) => Volatile.Write(ref _environment, environment);

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
