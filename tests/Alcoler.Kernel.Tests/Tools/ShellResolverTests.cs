using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Tools;
using Xunit;
using SpecialFolder = System.Environment.SpecialFolder;

namespace Alcoler.Kernel.Tests.Tools;

public sealed class ShellResolverTests
{
    [Fact]
    public void Windows_WithoutGit_OffersAndResolvesSystemCmd()
    {
        var host = new ResolverHost();
        var cmd = Assert.Single(host.Available());
        Assert.Equal(new ShellEnvironment(Path.Combine(host.System, "cmd.exe"), ShellDialect.Cmd), cmd);
        Assert.Equal(cmd, host.Resolve());
    }

    [Fact]
    public void Windows_UsesExistingComSpec_AndFallsBackWhenStale()
    {
        var host = new ResolverHost();
        var customCmd = Path.Combine(host.Root, "custom", "cmd.exe");
        host.Environment["ComSpec"] = customCmd;
        host.Files.Add(customCmd);
        Assert.Equal(customCmd, host.Resolve().FileName);
        host.Files.Remove(customCmd);
        Assert.Equal(Path.Combine(host.System, "cmd.exe"), host.Resolve().FileName);
    }

    [Fact]
    public void Windows_ValidCmdSnapshotSurvivesComSpecChange_AndStaleCmdUsesCurrentSystem()
    {
        var host = new ResolverHost();
        var savedCmd = new ShellEnvironment(Path.Combine(host.Root, "saved", "cmd.exe"), ShellDialect.Cmd);
        var currentCmd = Path.Combine(host.Root, "current", "cmd.exe");
        host.Environment["ComSpec"] = currentCmd;
        host.Files.UnionWith([savedCmd.FileName, currentCmd]);
        Assert.Equal(currentCmd, host.Available()[0].FileName);
        Assert.Equal(savedCmd, host.Resolve(snapshot: savedCmd));
        Assert.Equal(savedCmd, host.Resolve(preferred: savedCmd));
        host.Files.Remove(savedCmd.FileName);
        Assert.Equal(new ShellEnvironment(currentCmd, ShellDialect.Cmd), host.Resolve(snapshot: savedCmd));
        var notCmd = new ShellEnvironment(Path.Combine(host.Root, "not-cmd.exe"), ShellDialect.Cmd);
        host.Files.Add(notCmd.FileName);
        Assert.Equal(currentCmd, host.Resolve(snapshot: notCmd).FileName);
    }

    [Fact]
    public void Windows_AvailableShells_OffersOnlyOneGitBashPerInstallation()
    {
        var host = new ResolverHost();
        var bin = Path.Combine(host.ProgramFiles, "Git", "bin", "bash.exe");
        var usrBin = Path.Combine(host.ProgramFiles, "Git", "usr", "bin", "bash.exe");
        host.Files.UnionWith([bin, usrBin]);
        Assert.Equal(new[] { ShellDialect.Cmd, ShellDialect.GitBash }, host.Available().Select(shell => shell.Dialect));
        Assert.Equal(bin, host.Available()[1].FileName);
        Assert.Equal(new ShellEnvironment(usrBin, ShellDialect.GitBash),
            host.Resolve(snapshot: new(usrBin, ShellDialect.GitBash)));
        host.Files.Remove(bin);
        Assert.Equal(usrBin, host.Available()[1].FileName);
    }

    [Fact]
    public void Windows_AutoPrefersFixedGitThenGitOnPathThenExistingPathBash()
    {
        var host = new ResolverHost();
        var fixedBash = Path.Combine(host.ProgramFiles, "Git", "bin", "bash.exe");
        var gitDirectory = Path.Combine(host.Root, "portable git", "cmd");
        var derivedBash = Path.Combine(host.Root, "portable git", "bin", "bash.exe");
        var pathDirectory = Path.Combine(host.Root, "old bash", "bin");
        var pathBash = Path.Combine(pathDirectory, "bash.exe");
        host.Environment["PATH"] = $"\"{pathDirectory}\";{gitDirectory}";
        host.Files.UnionWith([fixedBash, derivedBash, pathBash, Path.Combine(gitDirectory, "git.exe")]);

        Assert.Equal(new[] { ShellDialect.Cmd, ShellDialect.GitBash }, host.Available().Select(shell => shell.Dialect));
        Assert.Equal(fixedBash, host.Resolve().FileName);
        host.Files.Remove(fixedBash);
        Assert.Equal(derivedBash, host.Resolve().FileName);
        host.Files.Remove(derivedBash);
        Assert.Equal(pathBash, host.Resolve().FileName);
        host.Files.Remove(pathBash);
        Assert.Equal(ShellDialect.Cmd, host.Resolve().Dialect);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("bin")]
    [InlineData("mingw64/bin")]
    public void Windows_DerivesPortableGitBashFromGitExecutable(string gitSubdirectory)
    {
        var host = new ResolverHost();
        var gitRoot = Path.Combine(host.Root, "Portable Git");
        var gitDirectory = Path.Combine(gitRoot, gitSubdirectory.Replace('/', Path.DirectorySeparatorChar));
        var bash = Path.Combine(gitRoot, "usr", "bin", "bash.exe");
        host.Environment["PATH"] = gitDirectory;
        host.Files.UnionWith([Path.Combine(gitDirectory, "git.exe"), bash]);
        Assert.Equal(bash, host.Resolve().FileName);
    }

    [Fact]
    public void Windows_SnapshotThenPreferenceThenAuto_ValidatesStaleBash()
    {
        var host = new ResolverHost();
        var auto = Path.Combine(host.ProgramFiles, "Git", "bin", "bash.exe");
        var preferred = new ShellEnvironment(Path.Combine(host.Root, "preferred", "bash.exe"), ShellDialect.GitBash);
        var snapshot = new ShellEnvironment(Path.Combine(host.Root, "snapshot", "bash.exe"), ShellDialect.GitBash);
        host.Files.UnionWith([auto, preferred.FileName, snapshot.FileName]);
        Assert.Equal(snapshot, host.Resolve(preferred, snapshot));
        host.Files.Remove(snapshot.FileName);
        Assert.Equal(preferred, host.Resolve(preferred, snapshot));
        host.Files.Remove(preferred.FileName);
        Assert.Equal(auto, host.Resolve(preferred, snapshot).FileName);
        Assert.Equal(ShellDialect.Cmd, host.Resolve(new("cmd.exe", ShellDialect.Cmd), snapshot).Dialect);
        Assert.Equal(ShellDialect.Cmd, host.Resolve(preferred, new("old-cmd.exe", ShellDialect.Cmd)).Dialect);
    }

    [Fact]
    public void Windows_RejectsWslLaunchers_InPathAndSnapshots()
    {
        var host = new ResolverHost();
        var aliasDirectory = Path.Combine(host.Profile, "AppData", "Local", "Microsoft", "WindowsApps");
        var systemBash = Path.Combine(host.System, "bash.exe");
        var aliasBash = Path.Combine(aliasDirectory, "bash.exe");
        host.Environment["PATH"] = $"{host.System};{aliasDirectory}";
        host.Files.UnionWith([systemBash, aliasBash]);
        Assert.Single(host.Available());
        Assert.Equal(ShellDialect.Cmd,
            host.Resolve(new(aliasBash, ShellDialect.GitBash), new(systemBash, ShellDialect.GitBash)).Dialect);
        Assert.Equal(ShellDialect.Cmd, host.Resolve(new("/bin/bash", ShellDialect.Posix)).Dialect);
    }

    [Fact]
    public void Windows_DiscoveryDeduplicatesCaseInsensitiveCandidates_AndChecksFilesEachTime()
    {
        var host = new ResolverHost();
        var directory = Path.Combine(host.ProgramFiles, "Git", "bin");
        var bash = Path.Combine(directory, "bash.exe");
        host.Environment["PATH"] = $"{directory};{directory.ToUpperInvariant()}";
        host.Files.Add(bash);
        Assert.Equal(2, host.Available().Count);
        host.Files.Remove(bash);
        Assert.Single(host.Available());
    }

    [Fact]
    public void Posix_IgnoresWindowsChoices_AndPreservesPosixSnapshot()
    {
        var host = new ResolverHost { IsWindows = false };
        host.Environment["PATH"] = "/usr/local/bin:/bin";
        host.Files.UnionWith([Path.Combine("/usr/local/bin", "bash"), Path.Combine("/bin", "bash"), "/bin/sh", "/custom/zsh"]);
        var cmd = new ShellEnvironment("cmd.exe", ShellDialect.Cmd);
        var gitBash = new ShellEnvironment("bash.exe", ShellDialect.GitBash);
        Assert.Equal(new ShellEnvironment(Path.Combine("/usr/local/bin", "bash"), ShellDialect.Posix), host.Resolve(cmd, gitBash));
        Assert.Equal(new ShellEnvironment("/custom/zsh", ShellDialect.Posix),
            host.Resolve(cmd, new("/custom/zsh", ShellDialect.Posix)));
        host.Files.Clear();
        Assert.Equal(new ShellEnvironment("/bin/sh", ShellDialect.Posix), host.Resolve(cmd, gitBash));
    }

    private sealed class ResolverHost
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "alcoler-shell-resolver");
        public string System => Path.Combine(Root, "Windows", "System32");
        public string ProgramFiles => Path.Combine(Root, "Program Files");
        public string Profile => Path.Combine(Root, "Users", "test");
        public bool IsWindows { get; init; } = true;
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ShellEnvironment> Available() => ShellResolver.AvailableShells(
            IsWindows, GetEnvironment, Files.Contains, GetFolder);

        public ShellEnvironment Resolve(ShellEnvironment? preferred = null, ShellEnvironment? snapshot = null) =>
            ShellResolver.ResolveEnvironment(preferred, snapshot, IsWindows, GetEnvironment, Files.Contains, GetFolder);

        private string? GetEnvironment(string key) => Environment.GetValueOrDefault(key);

        private string GetFolder(SpecialFolder folder) => folder switch
        {
            SpecialFolder.System => System,
            SpecialFolder.ProgramFiles => ProgramFiles,
            SpecialFolder.UserProfile => Profile,
            _ => "",
        };
    }
}
