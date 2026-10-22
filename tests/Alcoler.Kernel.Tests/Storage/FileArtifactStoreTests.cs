using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Identifiers;
using Xunit;

namespace Alcoler.Kernel.Tests.Storage;

public sealed class FileArtifactStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string ArtifactsRoot => Path.Combine(_temp.Root, "artifacts");

    [Fact]
    public async Task Save_WritesUnderSessionDirectory_AndRoundTripsBytes()
    {
        var store = NewStore();
        var sessionId = new SessionId("sess_roundtrip");
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var reference = await store.SaveAsync(sessionId, "capture.png", "image/png", data, CancellationToken.None);

        Assert.Equal("image/png", reference.MimeType);
        Assert.Equal("capture.png", reference.FileName);
        Assert.StartsWith("alcoler-artifact://sess_roundtrip/", reference.Uri, StringComparison.Ordinal);

        var storedFile = Assert.Single(Directory.GetFiles(Path.Combine(ArtifactsRoot, "sess_roundtrip")));
        var storedName = Path.GetFileName(storedFile);
        Assert.Matches("^[0-9a-f]{8}-capture\\.png$", storedName);
        Assert.Equal($"alcoler-artifact://sess_roundtrip/{storedName}", reference.Uri);

        var loaded = await store.TryLoadAsync(reference.Uri, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(data, loaded.Value.ToArray());
    }

    [Fact]
    public async Task Save_SameFileNameTwice_DoesNotOverwrite()
    {
        var store = NewStore();
        var sessionId = new SessionId("sess_twice");

        var first = await store.SaveAsync(sessionId, "log.txt", "text/plain", new byte[] { 1 }, CancellationToken.None);
        var second = await store.SaveAsync(sessionId, "log.txt", "text/plain", new byte[] { 2, 2 }, CancellationToken.None);

        Assert.NotEqual(first.Uri, second.Uri);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(ArtifactsRoot, "sess_twice")).Length);
        Assert.Equal([1], (await store.TryLoadAsync(first.Uri, CancellationToken.None))!.Value.ToArray());
        Assert.Equal([2, 2], (await store.TryLoadAsync(second.Uri, CancellationToken.None))!.Value.ToArray());
    }

    [Fact]
    public async Task Save_SanitizesUnsafeFileNameCharacters()
    {
        var store = NewStore();
        var sessionId = new SessionId("sess_sanitize");

        var reference = await store.SaveAsync(
            sessionId, "a<b>|c?.png", "image/png", new byte[] { 9 }, CancellationToken.None);

        Assert.Equal("a<b>|c?.png", reference.FileName);
        var storedName = Path.GetFileName(
            Assert.Single(Directory.GetFiles(Path.Combine(ArtifactsRoot, "sess_sanitize"))));
        Assert.Matches("^[0-9a-f]{8}-", storedName);
        Assert.All(
            Path.GetInvalidFileNameChars(),
            invalid => Assert.DoesNotContain(invalid, storedName));
        Assert.NotNull(await store.TryLoadAsync(reference.Uri, CancellationToken.None));
    }

    [Theory]
    [InlineData("file:///C:/tmp/x.png")]
    [InlineData("http://host/a/x.png")]
    [InlineData("https://host/a")]
    [InlineData("not a uri")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("alcoler-artifact://only")]
    [InlineData("alcoler-artifact://a/b/c.png")]
    [InlineData("alcoler-artifact://sess/%2e%2e/secret.png")]
    [InlineData("alcoler-artifact://sess/%2e%2e")]
    [InlineData("alcoler-artifact://sess/x%2fy.png")]
    public async Task TryLoad_ReturnsNull_ForForeignMalformedOrTraversingUris(string artifactUri)
    {
        var store = NewStore();

        Assert.Null(await store.TryLoadAsync(artifactUri, CancellationToken.None));
    }

    [Fact]
    public async Task TryLoad_MissingFile_ReturnsNull()
    {
        var store = NewStore();

        Assert.Null(await store.TryLoadAsync("alcoler-artifact://sess_none/missing.png", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteSessionArtifacts_RemovesOnlyTheRequestedSession()
    {
        var store = NewStore();
        var removed = await store.SaveAsync(
            new SessionId("sess_removed"), "removed.png", "image/png", new byte[] { 1 }, CancellationToken.None);
        var kept = await store.SaveAsync(
            new SessionId("sess_kept"), "kept.png", "image/png", new byte[] { 2 }, CancellationToken.None);

        await store.DeleteSessionArtifactsAsync(new SessionId("sess_removed"), CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(ArtifactsRoot, "sess_removed")));
        Assert.Null(await store.TryLoadAsync(removed.Uri, CancellationToken.None));
        Assert.NotNull(await store.TryLoadAsync(kept.Uri, CancellationToken.None));

        await store.DeleteSessionArtifactsAsync(new SessionId("sess_removed"), CancellationToken.None);
    }

    public void Dispose() => _temp.Dispose();

    private FileArtifactStore NewStore() => new(ArtifactsRoot);
}
