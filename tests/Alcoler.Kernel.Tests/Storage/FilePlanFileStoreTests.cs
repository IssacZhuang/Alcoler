using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Identifiers;
using Xunit;

namespace Alcoler.Kernel.Tests.Storage;

public sealed class FilePlanFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"alcoler-plan-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsPlan()
    {
        var store = new FilePlanFileStore(_root);
        var sessionId = StrongId.NewSessionId();

        await store.WriteAsync(sessionId, "# Plan", CancellationToken.None);
        var read = await store.ReadAsync(sessionId, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal("# Plan", read.Content);
        Assert.Equal($"plan-{sessionId.Value}.md", Path.GetFileName(read.Path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Write_ReplacesAnExistingPlan()
    {
        var store = new FilePlanFileStore(_root);
        var sessionId = StrongId.NewSessionId();

        await store.WriteAsync(sessionId, "first", CancellationToken.None);
        await store.WriteAsync(sessionId, "second", CancellationToken.None);

        var read = await store.ReadAsync(sessionId, CancellationToken.None);
        Assert.Equal("second", read!.Content);
    }

    [Fact]
    public async Task Read_MissingPlan_ReturnsNull()
    {
        var store = new FilePlanFileStore(_root);

        Assert.Null(await store.ReadAsync(StrongId.NewSessionId(), CancellationToken.None));
    }

    [Fact]
    public async Task UnsafeSessionIdCharacters_StayInsideThePlansRoot()
    {
        var store = new FilePlanFileStore(_root);

        var written = await store.WriteAsync(new SessionId("../../outside"), "plan", CancellationToken.None);

        Assert.Equal(Path.GetFullPath(_root), Path.GetFullPath(Path.GetDirectoryName(written.Path)!));
        Assert.True(File.Exists(written.Path));
    }

    [Fact]
    public async Task SessionIdWithoutSafeCharacters_Throws()
    {
        var store = new FilePlanFileStore(_root);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.WriteAsync(new SessionId("///"), "plan", CancellationToken.None));
    }
}
