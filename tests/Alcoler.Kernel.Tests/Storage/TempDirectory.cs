namespace Alcoler.Kernel.Tests.Storage;

internal sealed class TempDirectory : IDisposable
{
    public string Root { get; }

    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "alcoler-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    // Cleanup is best-effort: Windows can briefly hold handles on freshly closed files.
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
