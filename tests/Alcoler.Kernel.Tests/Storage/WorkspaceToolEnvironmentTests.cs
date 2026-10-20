using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Identifiers;
using Xunit;

namespace Alcoler.Kernel.Tests.Storage;

public sealed class WorkspaceToolEnvironmentTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public void Root_IsNormalizedFullPath()
    {
        var env = NewEnv();

        Assert.Equal(Path.GetFullPath(_temp.Root), env.Root);
    }

    [Fact]
    public void ResolvePath_NormalizesRelativePathsInsideRoot()
    {
        var env = NewEnv();

        var direct = env.ResolvePath("file.txt");
        var nested = env.ResolvePath("sub/../other.txt");
        var slashPath = env.ResolvePath("sub/file.txt");
        var rootedInside = env.ResolvePath(Path.Combine(env.Root, "file.txt"));

        Assert.Equal(Path.Combine(env.Root, "file.txt"), direct);
        Assert.Equal(Path.Combine(env.Root, "other.txt"), nested);
        Assert.Equal(Path.Combine(env.Root, "sub", "file.txt"), slashPath);
        Assert.Equal(Path.Combine(env.Root, "file.txt"), rootedInside);
    }

    [Theory]
    [InlineData("..\\escape.txt")]
    [InlineData("sub\\..\\..\\escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("..")]
    [InlineData("\\Windows\\System32\\cmd.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    public void ResolvePath_EscapingRoot_ThrowsInvalidOperationException(string path)
    {
        var env = NewEnv();

        Assert.Throws<InvalidOperationException>(() => env.ResolvePath(path));
    }

    [Fact]
    public void ResolvePath_Null_ThrowsArgumentNullException()
    {
        var env = NewEnv();

        Assert.Throws<ArgumentNullException>(() => env.ResolvePath(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolvePath_EmptyOrWhitespace_ThrowsArgumentException(string path)
    {
        var env = NewEnv();

        Assert.Throws<ArgumentException>(() => env.ResolvePath(path));
    }

    [Fact]
    public void ResolvePath_InvalidCharacters_ThrowArgumentException()
    {
        var env = NewEnv();
        var invalid = Path.GetInvalidPathChars()[0];

        Assert.Throws<ArgumentException>(() => env.ResolvePath("file" + invalid + ".txt"));
    }

    [Fact]
    public async Task SaveArtifactAsync_DelegatesToArtifactStore()
    {
        var store = new FileArtifactStore(Path.Combine(_temp.Root, "artifacts"));
        var env = new WorkspaceToolEnvironment(_temp.Root, store);
        var sessionId = new SessionId("sess_env");
        var data = new byte[] { 9, 8, 7 };

        var reference = await env.SaveArtifactAsync(sessionId, "notes.txt", "text/plain", data, CancellationToken.None);

        Assert.Equal("text/plain", reference.MimeType);
        Assert.Equal("notes.txt", reference.FileName);
        Assert.StartsWith("alcoler-artifact://sess_env/", reference.Uri, StringComparison.Ordinal);
        var loaded = await store.TryLoadAsync(reference.Uri, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(data, loaded.Value.ToArray());
    }

    public void Dispose() => _temp.Dispose();

    private WorkspaceToolEnvironment NewEnv() =>
        new(_temp.Root, new FileArtifactStore(Path.Combine(_temp.Root, "artifacts")));
}
