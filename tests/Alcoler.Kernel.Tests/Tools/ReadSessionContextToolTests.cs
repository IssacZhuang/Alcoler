using System.Text.Json;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public sealed class ReadSessionContextToolTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alcoler-rsc-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteSessionStore _store;

    public ReadSessionContextToolTests()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteSessionStore(Path.Combine(_root, "db.sqlite"));
        _store.InitializeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task MissingSession_ReportsNotFound()
    {
        var tool = new ReadSessionContextTool(_store);
        var result = await tool.ExecuteAsync(
            Context("""{"sessionId": "sess_missing", "query": "anything"}"""),
            CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Contains("Session sess_missing was not found.", result.SingleText());
    }

    [Fact]
    public async Task Relevance_PicksQueryMatchingMessages_InTranscriptOrder()
    {
        var session = await SeedSessionAsync(
            ("user", "please investigate the flaky login test"),
            ("assistant", "I looked at the database layer and found nothing"),
            ("user", "also check the GPU shader compilation path"),
            ("assistant", "the shader cache invalidation was the root cause of the flakiness"));

        var tool = new ReadSessionContextTool(_store);
        var result = await tool.ExecuteAsync(
            Context($$"""{"sessionId": "{{session.Value}}", "query": "shader compilation flakiness"}"""),
            CancellationToken.None);

        var text = result.SingleText();
        Assert.False(result.IsError);
        Assert.Contains("strategy: relevant", text);
        Assert.Contains("shader cache invalidation", text);
        Assert.DoesNotContain("database layer", text);
    }

    [Fact]
    public async Task Handoff_TakesTranscriptTail()
    {
        var session = await SeedSessionAsync(
            ("user", "first topic about apples"),
            ("assistant", "apples investigated"),
            ("user", "final topic about zebras"),
            ("assistant", "zebras investigated and closed out"));

        var tool = new ReadSessionContextTool(_store);
        var result = await tool.ExecuteAsync(
            Context($$"""{"sessionId": "{{session.Value}}", "query": "handoff", "strategy": "handoff"}"""),
            CancellationToken.None);

        var text = result.SingleText();
        Assert.Contains("strategy: handoff", text);
        Assert.Contains("zebras investigated", text);
    }

    [Fact]
    public void ParseSessionId_AcceptsBareGuidAndPrefix()
    {
        Assert.Equal("sess_abc", ReadSessionContextTool.ParseSessionId("abc").Value);
        Assert.Equal("sess_abc", ReadSessionContextTool.ParseSessionId("sess_abc").Value);
        Assert.Equal("sess_abc", ReadSessionContextTool.ParseSessionId("  sess_abc ").Value);
    }

    [Fact]
    public void Contract_UsesReferenceEnumAndDescriptions()
    {
        var contract = new ReadSessionContextTool(_store).Contract;

        Assert.Equal("ReadSessionContext", contract.Name);
        Assert.Equal(
            "Read relevant or handoff context from another persisted Alcoler session. Use when the user references #sess_* or asks to continue from a specific prior session.",
            contract.Description);

        var schema = contract.InputSchema.GetRawText();
        Assert.Contains("\"enum\": [\"relevant\", \"handoff\"]", schema);
        Assert.Contains("Use relevant for focused retrieval, or handoff for a bounded continuation summary.", schema);
        Assert.Contains("Target Alcoler session id to read from persisted session history.", schema);
    }

    private async Task<SessionId> SeedSessionAsync(params (string Role, string Text)[] turns)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new SessionRecord(
            StrongId.NewSessionId(),
            _root,
            "seed",
            Contracts.Permissions.PermissionMode.Yolo,
            null,
            now,
            now);
        await _store.UpsertSessionAsync(record, CancellationToken.None);
        long sequence = 0;
        foreach (var (role, text) in turns)
        {
            var message = new Contracts.Conversation.ConversationMessage(
                StrongId.NewMessageId(),
                role == "user" ? Contracts.Conversation.MessageRole.User : Contracts.Conversation.MessageRole.Assistant,
                [new Contracts.Conversation.TextPart(StrongId.NewPartId(), text)],
                now);
            await _store.AppendMessagesAsync(record.Id, [message], sequence++, CancellationToken.None);
        }
        return record.Id;
    }

    internal static ToolExecutionContext Context(string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return new ToolExecutionContext(
            new SessionId("sess_test"),
            Path.GetTempPath(),
            document.RootElement.Clone(),
            null!,
            TimeProvider.System);
    }
}
