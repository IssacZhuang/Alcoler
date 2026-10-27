using System.Diagnostics;
using Alcoler.Localization;

namespace Alcoler.Ui;

public enum ExternalEditorKind
{
    Application,
    FileManager,
}

public sealed record ExternalEditor(
    string Id,
    string Name,
    string Target,
    ExternalEditorKind Kind = ExternalEditorKind.Application);

public interface IExternalEditorLauncher
{
    IReadOnlyList<ExternalEditor> Editors { get; }

    void OpenDefault(string path);

    void Open(ExternalEditor editor, string path);
}

public sealed class ExternalEditorLauncher : IExternalEditorLauncher
{
    private readonly Lazy<IReadOnlyList<ExternalEditor>> _editors =
        new(DetectEditors, LazyThreadSafetyMode.ExecutionAndPublication);

    public IReadOnlyList<ExternalEditor> Editors => _editors.Value;

    public void OpenDefault(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void Open(ExternalEditor editor, string path)
    {
        if (editor.Kind == ExternalEditorKind.FileManager)
        {
            RevealInFileManager(editor, path);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            StartProcess("open", ["-a", editor.Target, path]);
            return;
        }

        StartProcess(editor.Target, [path]);
    }

    private static IReadOnlyList<ExternalEditor> DetectEditors()
    {
        var editors = OperatingSystem.IsWindows()
            ? DetectWindowsEditors()
            : OperatingSystem.IsMacOS()
                ? DetectMacEditors()
                : DetectLinuxEditors();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        return editors.Where(editor => seen.Add(editor.Target)).ToArray();
    }

    private static List<ExternalEditor> DetectWindowsEditors()
    {
        List<ExternalEditor> editors = [];
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0)
        {
            editors.Add(new ExternalEditor("explorer", Loc.Get("timeline_file_manager"),
                Path.Combine(windows, "explorer.exe"), ExternalEditorKind.FileManager));
        }

        AddWindowsEditor(editors, "vscode", "VS Code", "Code.exe", "code",
        [
            LocalProgramsPath("Microsoft VS Code", "Code.exe"),
            .. ProgramFilesPaths("Microsoft VS Code", "Code.exe"),
        ]);
        AddWindowsEditor(editors, "vscode-insiders", "VS Code Insiders", "Code - Insiders.exe", "code-insiders",
        [
            LocalProgramsPath("Microsoft VS Code Insiders", "Code - Insiders.exe"),
            .. ProgramFilesPaths("Microsoft VS Code Insiders", "Code - Insiders.exe"),
        ]);
        AddWindowsEditor(editors, "cursor", "Cursor", "Cursor.exe", "cursor",
        [
            LocalProgramsPath("Cursor", "Cursor.exe"),
            .. ProgramFilesPaths("Cursor", "Cursor.exe"),
        ]);
        AddWindowsEditor(editors, "trae", "Trae", "Trae.exe", null,
        [
            LocalProgramsPath("Trae", "Trae.exe"),
            LocalProgramsPath("Trae CN", "Trae.exe"),
            LocalProgramsPath("Trae CN", "Trae CN.exe"),
            .. ProgramFilesPaths("Trae", "Trae.exe"),
            .. ProgramFilesPaths("Trae CN", "Trae.exe"),
            .. ProgramFilesPaths("Trae CN", "Trae CN.exe"),
        ]);
        AddWindowsEditor(editors, "zed", "Zed", "Zed.exe", "zed",
        [
            LocalProgramsPath("Zed", "Zed.exe"),
            .. ProgramFilesPaths("Zed", "Zed.exe"),
        ]);
        AddWindowsEditor(editors, "windsurf", "Windsurf", "Windsurf.exe", "windsurf",
        [
            LocalProgramsPath("Windsurf", "Windsurf.exe"),
            .. ProgramFilesPaths("Windsurf", "Windsurf.exe"),
        ]);

        AddJetBrainsEditor(editors, "idea", "IntelliJ IDEA", "IntelliJ IDEA", "idea64.exe");
        AddJetBrainsEditor(editors, "webstorm", "WebStorm", "WebStorm", "webstorm64.exe");
        AddJetBrainsEditor(editors, "pycharm", "PyCharm", "PyCharm", "pycharm64.exe");
        AddJetBrainsEditor(editors, "goland", "GoLand", "GoLand", "goland64.exe");
        AddJetBrainsEditor(editors, "clion", "CLion", "CLion", "clion64.exe");
        AddJetBrainsEditor(editors, "rider", "Rider", "JetBrains Rider", "rider64.exe");
        AddJetBrainsEditor(editors, "phpstorm", "PhpStorm", "PhpStorm", "phpstorm64.exe");
        AddJetBrainsEditor(editors, "rubymine", "RubyMine", "RubyMine", "rubymine64.exe");
        AddJetBrainsEditor(editors, "datagrip", "DataGrip", "DataGrip", "datagrip64.exe");
        return editors;
    }

    private static List<ExternalEditor> DetectMacEditors()
    {
        List<ExternalEditor> editors =
        [
            new ExternalEditor("finder", Loc.Get("timeline_file_manager"),
                "/System/Library/CoreServices/Finder.app", ExternalEditorKind.FileManager),
        ];
        AddMacEditor(editors, "vscode", "VS Code", "/Applications/Visual Studio Code.app");
        AddMacEditor(editors, "vscode-insiders", "VS Code Insiders", "/Applications/Visual Studio Code - Insiders.app");
        AddMacEditor(editors, "cursor", "Cursor", "/Applications/Cursor.app");
        AddMacEditor(editors, "trae", "Trae", "/Applications/Trae.app");
        AddMacEditor(editors, "zed", "Zed", "/Applications/Zed.app");
        AddMacEditor(editors, "sublime", "Sublime Text", "/Applications/Sublime Text.app");
        AddMacEditor(editors, "codebuddy", "CodeBuddy", "/Applications/CodeBuddy.app");
        AddMacEditor(editors, "qoder", "Qoder", "/Applications/Qoder.app");
        AddMacEditor(editors, "idea", "IntelliJ IDEA", "/Applications/IntelliJ IDEA.app");
        AddMacEditor(editors, "idea-ce", "IntelliJ IDEA CE", "/Applications/IntelliJ IDEA CE.app");
        AddMacEditor(editors, "webstorm", "WebStorm", "/Applications/WebStorm.app");
        AddMacEditor(editors, "pycharm", "PyCharm", "/Applications/PyCharm.app");
        AddMacEditor(editors, "goland", "GoLand", "/Applications/GoLand.app");
        AddMacEditor(editors, "phpstorm", "PhpStorm", "/Applications/PhpStorm.app");
        AddMacEditor(editors, "rider", "Rider", "/Applications/Rider.app");
        AddMacEditor(editors, "clion", "CLion", "/Applications/CLion.app");
        AddMacEditor(editors, "rubymine", "RubyMine", "/Applications/RubyMine.app");
        AddMacEditor(editors, "datagrip", "DataGrip", "/Applications/DataGrip.app");
        return editors;
    }

    private static List<ExternalEditor> DetectLinuxEditors()
    {
        List<ExternalEditor> editors = [];
        if (FindExecutableOnPath("xdg-open", "xdg-open") is { } xdgOpen)
        {
            editors.Add(new ExternalEditor("file-manager", Loc.Get("timeline_file_manager"),
                xdgOpen, ExternalEditorKind.FileManager));
        }

        AddLinuxEditor(editors, "vscode", "VS Code", "code");
        AddLinuxEditor(editors, "vscode-insiders", "VS Code Insiders", "code-insiders");
        AddLinuxEditor(editors, "cursor", "Cursor", "cursor");
        AddLinuxEditor(editors, "zed", "Zed", "zed");
        AddLinuxEditor(editors, "windsurf", "Windsurf", "windsurf");
        AddLinuxEditor(editors, "sublime", "Sublime Text", "subl");
        AddLinuxEditor(editors, "codebuddy", "CodeBuddy", "codebuddy");
        AddLinuxEditor(editors, "qoder", "Qoder", "qoder");
        AddLinuxEditor(editors, "idea", "IntelliJ IDEA", "idea");
        AddLinuxEditor(editors, "webstorm", "WebStorm", "webstorm");
        AddLinuxEditor(editors, "pycharm", "PyCharm", "pycharm");
        AddLinuxEditor(editors, "goland", "GoLand", "goland");
        AddLinuxEditor(editors, "clion", "CLion", "clion");
        AddLinuxEditor(editors, "rider", "Rider", "rider");
        AddLinuxEditor(editors, "phpstorm", "PhpStorm", "phpstorm");
        AddLinuxEditor(editors, "rubymine", "RubyMine", "rubymine");
        AddLinuxEditor(editors, "datagrip", "DataGrip", "datagrip");
        return editors;
    }

    private static void AddWindowsEditor(
        List<ExternalEditor> editors,
        string id,
        string name,
        string executableName,
        string? command,
        IReadOnlyList<string?> candidates)
    {
        var executable = candidates.FirstOrDefault(candidate => candidate is not null && File.Exists(candidate))
            ?? FindExecutableOnPath(command, executableName);
        if (executable is not null)
        {
            editors.Add(new ExternalEditor(id, name, executable));
        }
    }

    private static void AddMacEditor(List<ExternalEditor> editors, string id, string name, string bundlePath)
    {
        if (Directory.Exists(bundlePath))
        {
            editors.Add(new ExternalEditor(id, name, bundlePath));
        }
    }

    private static void AddLinuxEditor(List<ExternalEditor> editors, string id, string name, string command)
    {
        if (FindExecutableOnPath(command, command) is { } executable)
        {
            editors.Add(new ExternalEditor(id, name, executable));
        }
    }

    private static void AddJetBrainsEditor(
        List<ExternalEditor> editors,
        string id,
        string name,
        string directoryPrefix,
        string executableName)
    {
        List<string> roots = [];
        if (LocalProgramsPath("JetBrains") is { } local)
        {
            roots.Add(local);
        }
        roots.AddRange(ProgramFilesPaths("JetBrains"));

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] products;
            try
            {
                products = Directory.GetDirectories(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var executable = products
                .Where(product => Path.GetFileName(product).StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(product => Path.GetFileName(product), StringComparer.OrdinalIgnoreCase)
                .Select(product => Path.Combine(product, "bin", executableName))
                .FirstOrDefault(File.Exists);
            if (executable is not null)
            {
                editors.Add(new ExternalEditor(id, name, executable));
                return;
            }
        }
    }

    private static string? LocalProgramsPath(params string[] segments)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return root.Length == 0 ? null : Path.Combine([root, "Programs", .. segments]);
    }

    private static IReadOnlyList<string> ProgramFilesPaths(params string[] segments)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        return roots
            .Where(root => root.Length > 0)
            .Select(root => Path.Combine([root, .. segments]))
            .ToArray();
    }

    private static string? FindExecutableOnPath(string? command, string executableName)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string[] extensions;
        if (OperatingSystem.IsWindows())
        {
            extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else
        {
            extensions = [string.Empty];
        }

        foreach (var directory in path.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension.ToLowerInvariant());
                if (!File.Exists(candidate))
                {
                    continue;
                }

                if (!OperatingSystem.IsWindows() || candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                // CLI shims such as Microsoft VS Code\bin\code.cmd sit one level below the application root.
                if (Path.GetDirectoryName(candidate) is not { } shimDirectory)
                {
                    continue;
                }
                foreach (var appRoot in new[] { shimDirectory, Path.GetDirectoryName(shimDirectory) })
                {
                    if (appRoot is null)
                    {
                        continue;
                    }
                    var executable = Path.Combine(appRoot, executableName);
                    if (File.Exists(executable))
                    {
                        return executable;
                    }
                }
            }
        }
        return null;
    }

    private static void RevealInFileManager(ExternalEditor editor, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo(editor.Target) { UseShellExecute = true };
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            StartProcess("open", ["-R", path]);
            return;
        }

        var directory = Path.GetDirectoryName(path);
        StartProcess(editor.Target, [string.IsNullOrEmpty(directory) ? path : directory]);
    }

    private static void StartProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start);
    }
}
