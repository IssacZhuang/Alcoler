using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Cake.Common;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Build;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Common.Tools.DotNet.Restore;
using Cake.Common.Tools.DotNet.Test;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Frosting;
using IOPath = System.IO.Path;

public static class Program
{
    public static int Main(string[] args)
    {
        return new CakeHost()
            .UseContext<BuildContext>()
            .Run(args);
    }
}

public sealed class BuildContext : FrostingContext
{
    public string BuildConfiguration { get; }
    public string TargetRuntime { get; }
    public string RepoRoot { get; }
    public string Solution { get; }
    public string AppProject { get; }
    public string OutputDir { get; }
    public string PackageDir { get; }
    public string Version { get; }

    public BuildContext(ICakeContext context)
        : base(context)
    {
        // The pipeline intentionally produces Release packages only; AOT is not supported.
        BuildConfiguration = "Release";
        TargetRuntime = context.Arguments.GetArgument("runtime") ?? "win-x64";
        RepoRoot = FindRepoRoot();
        Solution = IOPath.Combine(RepoRoot, "Alcoler.slnx");
        AppProject = IOPath.Combine(RepoRoot, "src", "Alcoler", "Alcoler.csproj");
        OutputDir = IOPath.Combine(RepoRoot, "artifacts", BuildConfiguration, "Publish", TargetRuntime);
        PackageDir = IOPath.Combine(RepoRoot, "artifacts", BuildConfiguration, "Package");
        Version = ReadVersion(RepoRoot);
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(IOPath.Combine(directory.FullName, "Alcoler.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException(
            "Alcoler.slnx was not found in the working directory or any parent directory.");
    }

    private static string ReadVersion(string repoRoot)
    {
        var propsPath = IOPath.Combine(repoRoot, "Directory.Build.props");
        var version = XDocument.Load(propsPath)
            .Root?
            .Elements("PropertyGroup")
            .Elements("Version")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => value.Length > 0);
        return version ?? throw new InvalidOperationException(
            $"Version is not set in {propsPath}.");
    }
}

[TaskName("Restore")]
public sealed class RestoreTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.Log.Information($"Restore {context.Solution}");
        // Restore for the target runtime. A RID-less restore would be rewritten by
        // the publish restore, recompiling every project while the shared compiler
        // server still holds the build outputs, failing with CS2012.
        context.DotNetRestore(context.Solution, new DotNetRestoreSettings
        {
            Runtime = context.TargetRuntime
        });
    }
}

[TaskName("Build")]
[IsDependentOn(typeof(RestoreTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.Log.Information($"Build {context.Solution} ({context.BuildConfiguration})");
        context.DotNetBuild(context.Solution, new DotNetBuildSettings
        {
            Configuration = context.BuildConfiguration,
            NoRestore = true,
            ArgumentCustomization = args => args
                .Append("-p:UseSharedCompilation=false")
                // Parallel nodes duplicate-build the same RID-less reference
                // and race on its obj output (CS2012); serialize instead.
                .Append("-m:1")
        });
    }
}

[TaskName("Test")]
[IsDependentOn(typeof(BuildTask))]
public sealed class TestTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.Log.Information($"Test {context.Solution} ({context.BuildConfiguration})");
        context.DotNetTest(context.Solution, new DotNetTestSettings
        {
            Configuration = context.BuildConfiguration,
            NoBuild = true,
            NoRestore = true
        });
    }
}

[TaskName("Publish")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PublishTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.Log.Information(
            $"Publish {context.AppProject} ({context.BuildConfiguration}, {context.TargetRuntime}) -> {context.OutputDir}");
        context.DotNetPublish(context.AppProject, new DotNetPublishSettings
        {
            Configuration = context.BuildConfiguration,
            Runtime = context.TargetRuntime,
            OutputDirectory = context.OutputDir,
            ArgumentCustomization = args => args
                .Append("/p:SelfContained=true")
                .Append("/p:PublishSingleFile=true")
                .Append("/p:DebugType=embedded")
                // Publish recompiles the projects the Build task just wrote; the
                // shared compiler server still holds those outputs and fails with CS2012.
                .Append("/p:UseSharedCompilation=false")
                // Parallel nodes duplicate-build the same RID-less reference
                // and race on its obj output (CS2012); serialize instead.
                .Append("/m:1")
        });
    }
}

[TaskName("Package")]
[IsDependentOn(typeof(PublishTask))]
public sealed class PackageTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        if (!Directory.Exists(context.OutputDir) ||
            Directory.GetFiles(context.OutputDir, "*", SearchOption.AllDirectories).Length == 0)
        {
            throw new DirectoryNotFoundException(
                $"Publish output is missing or empty: {context.OutputDir}");
        }

        Directory.CreateDirectory(context.PackageDir);
        var packageName = $"Alcoler-{context.Version}-{context.TargetRuntime}";
        var packagePath = IOPath.Combine(context.PackageDir, $"{packageName}.zip");
        var unpackedDir = IOPath.Combine(context.PackageDir, $"{packageName}-unpacked");
        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        foreach (var pdbPath in Directory.GetFiles(context.OutputDir, "*.pdb", SearchOption.AllDirectories))
        {
            context.Log.Information($"Removing symbol file from package: {pdbPath}");
            File.Delete(pdbPath);
        }

        context.Log.Information($"Packaging output -> {packagePath}");
        CreatePackageZip(context, context.OutputDir, packagePath);

        if (Directory.Exists(unpackedDir))
        {
            Directory.Delete(unpackedDir, recursive: true);
        }
        context.Log.Information($"Staging unpacked output -> {unpackedDir}");
        CopyDirectory(context.OutputDir, unpackedDir);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(
                IOPath.Combine(destinationDir, IOPath.GetRelativePath(sourceDir, directory)));
        }
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            File.Copy(
                file,
                IOPath.Combine(destinationDir, IOPath.GetRelativePath(sourceDir, file)),
                overwrite: true);
        }
    }

    private static void CreatePackageZip(BuildContext context, string outputDir, string packagePath)
    {
        const int modeExecutable = unchecked((int)0x81ED0000); // 0755
        const int modeRegular = unchecked((int)0x81A40000);    // 0644

        // Windows-created zip entries carry no unix mode; without it the app
        // binary and ripgrep are not executable after extraction on unix.
        var unixModes = context.TargetRuntime.StartsWith("osx") || context.TargetRuntime.StartsWith("linux");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var filePath in Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories))
        {
            var entryName = IOPath.GetRelativePath(outputDir, filePath).Replace('\\', '/');
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            if (unixModes)
            {
                var fileName = IOPath.GetFileNameWithoutExtension(filePath);
                var inRootDirectory = IOPath.GetDirectoryName(IOPath.GetFullPath(filePath))
                    == IOPath.GetFullPath(outputDir);
                var executable = fileName == "rg" ||
                    (inRootDirectory && IOPath.GetExtension(filePath).Length == 0);
                entry.ExternalAttributes = executable ? modeExecutable : modeRegular;
            }
            using var source = File.OpenRead(filePath);
            using var target = entry.Open();
            source.CopyTo(target);
        }
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(PackageTask))]
public sealed class DefaultTask : FrostingTask
{
}
