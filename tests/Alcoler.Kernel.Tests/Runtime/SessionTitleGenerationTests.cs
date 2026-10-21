using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tests.Permissions;
using Alcoler.Kernel.Tests.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SessionTitleGenerationTests
{
    [Theory]
    [InlineData("  Fix\t the\r\nlogin  ", "Fix the login")]
    [InlineData("你好", "你好")]
    [InlineData("123456789012345678901234567890123456789012345678901234567890", "123456789012345678901234567890123456789012345678901234567890")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901", "123456789012345678901234567890123456789012345678901234567...")]
    public async Task GenerationDisabledByDefault_FirstInputStillGetsNormalizedFallback(string input, string expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new TitleModel();
        await using var runtime = fixture.Runtime(model, enabled: null);
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        var initial = await fixture.RecordAsync(session.Id);
        Assert.Equal(SessionTitleSource.Default, initial.TitleSource);
        Assert.Equal(session.Title, initial.Title);
        using var projection = runtime.Subscribe(session.Id);
        var changes = ObserveTitles(runtime);

        await SendAsync(runtime, session.Id, input);
        await WaitForTurnAsync(projection, 1);
        var fallback = await fixture.RecordAsync(session.Id);
        Assert.Equal(expected, fallback.Title);
        Assert.Equal(SessionTitleSource.FirstInput, fallback.TitleSource);
        Assert.Equal(expected, projection.Snapshot().State.Title);
        Assert.Equal((session.Id, expected), Assert.Single(changes));

        await SendAsync(runtime, session.Id, "A later request must not replace the first input title");
        await WaitForTurnAsync(projection, 2);
        await runtime.DisposeAsync();
        Assert.Empty(model.TitleRequests);
        Assert.Equal(expected, (await fixture.RecordAsync(session.Id)).Title);
        Assert.Single(changes);
    }

    [Theory]
    [InlineData("123456789", false)]
    [InlineData("一二三四五六七八九", false)]
    [InlineData("𠮷𠮷𠮷𠮷𠮷𠮷𠮷𠮷𠮷", false)]
    [InlineData("一二三四五六七八九十", true)]
    [InlineData("𠮷𠮷𠮷𠮷𠮷𠮷𠮷𠮷𠮷𠮷", true)]
    public async Task GenerationThreshold_CountsRunesAndDoesNotRetryShortFirstInputs(string input, bool generates)
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new TitleModel();
        await using var runtime = fixture.Runtime(model);
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAsync(runtime, session.Id, input);
        await WaitForTurnAsync(projection, 1);
        if (generates)
            await WaitForTitleAsync(fixture.Store, session.Id, SessionTitleSource.Generated);

        await SendAsync(runtime, session.Id, "This later request has more than ten runes");
        await WaitForTurnAsync(projection, 2);
        await runtime.DisposeAsync();
        Assert.Equal(generates ? 1 : 0, model.TitleRequests.Count);
        var record = await fixture.RecordAsync(session.Id);
        Assert.Equal(generates ? "Generated session title" : input, record.Title);
        Assert.Equal(generates ? SessionTitleSource.Generated : SessionTitleSource.FirstInput, record.TitleSource);
    }

    [Fact]
    public async Task RejectedWhitespace_DoesNotConsumeFirstRealInput()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new TitleModel();
        await using var runtime = fixture.Runtime(model);
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        var rejected = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, " \t\r\n "), CancellationToken.None);
        Assert.False(rejected.Accepted);
        Assert.Equal(SessionTitleSource.Default, (await fixture.RecordAsync(session.Id)).TitleSource);
        Assert.Empty(model.MainRequests);
        Assert.Empty(model.TitleRequests);

        using var projection = runtime.Subscribe(session.Id);
        await SendAsync(runtime, session.Id, "Implement the first valid request");
        await WaitForTurnAsync(projection, 1);
        await WaitForTitleAsync(fixture.Store, session.Id, SessionTitleSource.Generated);
        Assert.Single(model.TitleRequests);
    }

    [Fact]
    public async Task SlowTitle_DoesNotBlockMainTurn_AndPersistsAcrossReopenWithoutRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var clock = new ManualTimeProvider();
        var model = new TitleModel { BlockTitle = true };
        await using var runtime = fixture.Runtime(model, configure: options => options with { Clock = clock });
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var changes = ObserveTitles(runtime);
        const string input = "  Diagnose\t SQLite\n connection leaks  ";
        const string fallback = "Diagnose SQLite connection leaks";

        await SendAsync(runtime, session.Id, input);
        await model.TitleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForTurnAsync(projection, 1);
        Assert.False(model.TitleFinished.Task.IsCompleted);
        var before = await fixture.RecordAsync(session.Id);
        Assert.Equal(fallback, before.Title);
        Assert.Equal(SessionTitleSource.FirstInput, before.TitleSource);
        Assert.Equal(fallback, projection.Snapshot().State.Title);
        Assert.Equal((session.Id, fallback), Assert.Single(changes));
        Assert.Equal(fallback, Assert.Single(await runtime.ListSessionsAsync(fixture.Root, 10, CancellationToken.None)).Title);
        Assert.Equal(fallback, (await runtime.ResumeSessionAsync(session.Id, CancellationToken.None)).Title);

        clock.Advance(TimeSpan.FromHours(1));
        model.TitleRelease.TrySetResult();
        var generated = await WaitForTitleAsync(fixture.Store, session.Id, SessionTitleSource.Generated);
        await WaitUntilAsync(() => changes.Count == 2 && projection.Snapshot().State.Title == generated.Title);
        Assert.Equal(before with { Title = generated.Title, TitleSource = SessionTitleSource.Generated }, generated);
        Assert.Equal(new[] { (session.Id, fallback), (session.Id, generated.Title) }, changes.ToArray());
        await SendAsync(runtime, session.Id, "Continue diagnosing the same connection leak");
        await WaitForTurnAsync(projection, 2);
        var persisted = await fixture.RecordAsync(session.Id);
        await runtime.DisposeAsync();
        await fixture.Store.DisposeAsync();

        await using var reopenedStore = new SqliteSessionStore(fixture.DatabasePath);
        await reopenedStore.InitializeAsync(CancellationToken.None);
        Assert.Equal(persisted, await reopenedStore.GetSessionAsync(session.Id, CancellationToken.None));
        await using var reopened = fixture.Runtime(model, store: reopenedStore);
        var reopenedChanges = ObserveTitles(reopened);
        var resumed = await reopened.ResumeSessionAsync(session.Id, CancellationToken.None);
        Assert.Equal(generated.Title, resumed.Title);
        using var resumedProjection = reopened.Subscribe(session.Id);
        Assert.Equal(generated.Title, resumedProjection.Snapshot().State.Title);
        await SendAsync(reopened, session.Id, "Continue after reopening the persisted session");
        await WaitForTurnAsync(resumedProjection, 3);
        await reopened.DisposeAsync();
        Assert.Single(model.TitleRequests);
        Assert.Empty(reopenedChanges);
        Assert.Equal(generated.Title, (await reopenedStore.GetSessionAsync(session.Id, CancellationToken.None))!.Title);
    }

    [Fact]
    public async Task TitlePrompt_ContainsOnlyDedicatedInstructionsAndBoundedFirstRealInput()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new TitleModel { BlockTitle = true };
        SessionHandle session;
        await using (var creator = fixture.Runtime(model))
            session = await creator.CreateSessionAsync(new SessionOptions(fixture.Root, PermissionMode: PermissionMode.Plan), CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        ConversationMessage[] history =
        [
            new(StrongId.NewMessageId(), MessageRole.Assistant,
                [new TextPart(StrongId.NewPartId(), "private assistant history")], now),
            new(StrongId.NewMessageId(), MessageRole.User,
                [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder,
                    "private task notification", ProviderVisible: true)], now, Source: MessageSource.TaskNotification),
        ];
        await fixture.Store.AppendMessagesAsync(session.Id, history, 1, CancellationToken.None);
        var tool = new FakeTool("PrivateTool");
        await using var runtime = fixture.Runtime(model, configure: options => options with
        {
            AgentInstructions = "private agent instructions",
            CustomSystemPrompt = "private main system prompt",
            SkillsListing = "private skills listing",
            Tools = [tool],
        });
        await runtime.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var normalized = "Investigate SQLite locks " + new string('x', 1_300);
        await SendAsync(runtime, session.Id, " \tInvestigate\r\nSQLite  locks\t" + new string('x', 1_300) + "\n ");
        await model.TitleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForTurnAsync(projection, 2);

        var request = Assert.Single(model.TitleRequests);
        Assert.Empty(request.Tools);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(MessageRole.System, request.Messages[0].Role);
        var system = Assert.IsType<InputTextBlock>(Assert.Single(request.Messages[0].Content)).Text;
        Assert.Contains("title-generation task", system);
        Assert.Contains("Never answer", system);
        Assert.Contains("primary language", system);
        Assert.Contains("{\"title\":\"...\"}", system);
        Assert.Equal(MessageRole.User, request.Messages[1].Role);
        Assert.Equal(normalized[..1_200], Assert.IsType<InputTextBlock>(Assert.Single(request.Messages[1].Content)).Text);
        var main = Assert.Single(model.MainRequests);
        Assert.Contains(main.Tools, contract => contract.Name == "PrivateTool");
        Assert.Contains(main.Messages.SelectMany(message => message.Content).OfType<InputTextBlock>(),
            block => block.Text.Contains("private assistant history", StringComparison.Ordinal));
        Assert.DoesNotContain("private", system, StringComparison.OrdinalIgnoreCase);
        model.TitleRelease.TrySetResult();
        await WaitForTitleAsync(fixture.Store, session.Id, SessionTitleSource.Generated);
        Assert.Empty(tool.ExecutedContexts);
    }

    [Theory]
    [InlineData("session", 3_000, 3_000)]
    [InlineData("switch", 32_000, 5_000)]
    [InlineData("send", 32_000, 5_000)]
    [InlineData("default", 0, 5_000)]
    public async Task TitleRequest_UsesCurrentSelectionLowestReasoningAndIndependentOutputCap(string selectionKind, int outputLimit, int expectedCap)
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = new TitleModel("original");
        var selected = new TitleModel("selected", outputLimit == 0 ? null : outputLimit);
        var selection = new ModelSelection("selected", "title-model", new ModelCallOptions("high", 123));
        await using var runtime = fixture.Runtime(original, configure: options => options with
        {
            Providers = [new ScriptedModelProvider(original, "original"), new ScriptedModelProvider(selected, "selected")],
            DefaultModel = selectionKind == "default" ? selection : new ModelSelection("original", "title-model"),
        });
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root,
            ModelSelection: selectionKind == "session" ? selection : null), CancellationToken.None);
        if (selectionKind == "switch")
        {
            var switched = await runtime.SubmitAsync(
                new SwitchModelCommand(StrongId.NewCommandId(), session.Id, 0, selection), CancellationToken.None);
            Assert.True(switched.Accepted, switched.RejectionReason);
        }
        using var projection = runtime.Subscribe(session.Id);
        await SendAsync(runtime, session.Id, "Implement model selection handling", selectionKind == "send" ? selection : null);
        await WaitForTurnAsync(projection, 1);
        await WaitForTitleAsync(fixture.Store, session.Id, SessionTitleSource.Generated);

        Assert.Empty(original.TitleRequests);
        Assert.Empty(original.MainRequests);
        var title = Assert.Single(selected.TitleRequests);
        Assert.Equal(new ModelCallOptions("low", expectedCap), title.Options);
        Assert.Empty(title.Tools);
        Assert.Equal(selection.Options, Assert.Single(selected.MainRequests).Options);
        Assert.Equal(selection, (await fixture.RecordAsync(session.Id)).ModelSelection);
    }

    public static IEnumerable<object[]> CleanResponses()
    {
        yield return ["{\"title\":\"  Fix   SQLite locks.  \"}", "Fix SQLite locks"];
        yield return ["<think>Ignore this private reasoning.</think>\n{\"title\":\"Fix SQLite locks\"}", "Fix SQLite locks"];
        yield return ["```json\n{\"title\":\"Fix SQLite locks\"}\n```", "Fix SQLite locks"];
        yield return ["### \"Fix SQLite locks.\"\nAn explanation that is not part of the title", "Fix SQLite locks"];
        yield return ["\"修复数据库连接泄漏。\"", "修复数据库连接泄漏"];
        yield return [JsonSerializer.Serialize(new { title = new string('x', 130) }), new string('x', 97) + "..."];
    }

    [Theory]
    [MemberData(nameof(CleanResponses))]
    public async Task GeneratedTitle_CleansProviderFormattingAndCapsLength(string response, string expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new TitleModel { TitleText = response };
        await using var runtime = fixture.Runtime(model);
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAsync(runtime, session.Id, "Investigate SQLite connection locking");
        await WaitForTurnAsync(projection, 1);
        var record = await WaitForTitleAsync(fixture.Store, session.Id, SessionTitleSource.Generated);
        Assert.Equal(expected, record.Title);
        Assert.InRange(record.Title.Length, 1, 100);
        Assert.Single(model.TitleRequests);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("punctuation")]
    [InlineData("missing-title")]
    [InlineData("numeric-title")]
    [InlineData("null-title")]
    [InlineData("malformed-json")]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("tool")]
    public async Task InvalidOrFailedTitle_LeavesFallbackAndNeverRetriesOnLaterTurnsOrResume(string responseKind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var tool = new FakeTool("PrivateTool");
        var model = new TitleModel
        {
            ThrowTitle = responseKind == "exception",
            TitleEvents = responseKind switch
            {
                "failure" => [new ModelStreamFailed(new ModelFailure(ModelFailureKind.AuthenticationFailed, "title key rejected"))],
                "tool" => [new ModelTextDelta("Do not accept this title"),
                    new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), "PrivateTool", JsonSerializer.SerializeToElement(new { }))),
                    new ModelStreamFinished(ModelFinishReason.ToolCalls)],
                _ => null,
            },
            TitleText = responseKind switch
            {
                "empty" => "",
                "punctuation" => " ... !!! ",
                "missing-title" => "{\"other\":\"Not a title\"}",
                "numeric-title" => "{\"title\":42}",
                "null-title" => "{\"title\":null}",
                "malformed-json" => "{\"title\":",
                _ => "unused",
            },
        };
        await using var runtime = fixture.Runtime(model, configure: options => options with { Tools = [tool] });
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var changes = ObserveTitles(runtime);
        const string fallback = "Investigate invalid title responses";
        await SendAsync(runtime, session.Id, fallback);
        await WaitForTurnAsync(projection, 1);
        await WaitForRejectedTitleAsync(model);
        Assert.Equal(SessionTitleSource.FirstInput, (await fixture.RecordAsync(session.Id)).TitleSource);
        await SendAsync(runtime, session.Id, "A later turn must not retry failed title generation");
        await WaitForTurnAsync(projection, 2);
        await runtime.DisposeAsync();

        await using var reopened = fixture.Runtime(model);
        await reopened.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var resumedProjection = reopened.Subscribe(session.Id);
        await SendAsync(reopened, session.Id, "A resumed session must not retry title generation either");
        await WaitForTurnAsync(resumedProjection, 3);
        await reopened.DisposeAsync();
        var record = await fixture.RecordAsync(session.Id);
        Assert.Equal(fallback, record.Title);
        Assert.Equal(SessionTitleSource.FirstInput, record.TitleSource);
        Assert.Single(model.TitleRequests);
        Assert.Equal((session.Id, fallback), Assert.Single(changes));
        Assert.Empty(tool.ExecutedContexts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRename_ProtectsAgainstLateTitle_EvenWhenNameMatchesFallback(bool sameAsFallback)
    {
        await using var fixture = await Fixture.CreateAsync();
        var clock = new ManualTimeProvider();
        var model = new TitleModel { BlockTitle = true };
        await using var runtime = fixture.Runtime(model, configure: options => options with { Clock = clock });
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var changes = ObserveTitles(runtime);
        const string fallback = "Investigate manual rename concurrency";
        await SendAsync(runtime, session.Id, fallback);
        await model.TitleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForTurnAsync(projection, 1);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await runtime.SubmitAsync(
            new SetModeCommand(StrongId.NewCommandId(), session.Id, 0, PermissionMode.Plan), CancellationToken.None)).Accepted);
        var beforeRename = await fixture.RecordAsync(session.Id);
        clock.Advance(TimeSpan.FromHours(1));
        var manual = sameAsFallback ? fallback : "My chosen session title";
        var renamed = await runtime.SubmitAsync(
            new RenameSessionCommand(StrongId.NewCommandId(), session.Id, 0, manual), CancellationToken.None);
        Assert.True(renamed.Accepted, renamed.RejectionReason);
        var expected = beforeRename with { Title = manual, TitleSource = SessionTitleSource.Custom };
        Assert.Equal(expected, await fixture.RecordAsync(session.Id));

        model.TitleRelease.TrySetResult();
        await WaitForRejectedTitleAsync(model);
        Assert.Equal(expected, await fixture.RecordAsync(session.Id));
        Assert.Equal(manual, projection.Snapshot().State.Title);
        Assert.Equal(new[] { (session.Id, fallback), (session.Id, manual) }, changes.ToArray());
        await runtime.DisposeAsync();
        await using var reopened = fixture.Runtime(model);
        Assert.Equal(manual, (await reopened.ResumeSessionAsync(session.Id, CancellationToken.None)).Title);
        Assert.Equal(expected, await fixture.RecordAsync(session.Id));
        Assert.Single(model.TitleRequests);
    }

    [Theory]
    [InlineData(false, "Explicit session title")]
    [InlineData(false, "New session")]
    [InlineData(false, "")]
    [InlineData(true, null)]
    [InlineData(true, "Explicit child title")]
    public async Task ChildAndExplicitlyTitledSessions_SuppressFallbackAndGeneration(bool child, string? title)
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new TitleModel();
        await using var runtime = fixture.Runtime(model);
        SessionId? parent = child
            ? (await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None)).Id
            : null;
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root, Title: title, ParentSessionId: parent), CancellationToken.None);
        var initial = await fixture.RecordAsync(session.Id);
        using var projection = runtime.Subscribe(session.Id);
        var changes = ObserveTitles(runtime);
        await SendAsync(runtime, session.Id, "Implement a meaningful task with more than ten runes");
        await WaitForTurnAsync(projection, 1);
        await runtime.DisposeAsync();
        var record = await fixture.RecordAsync(session.Id);
        Assert.Equal(initial.Title, record.Title);
        Assert.Equal(title is null ? SessionTitleSource.Default : SessionTitleSource.Custom, record.TitleSource);
        Assert.Equal(parent, record.ParentSessionId);
        Assert.Empty(model.TitleRequests);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task MainStop_DoesNotCancelIndependentTitleGeneration()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new TitleModel { BlockTitle = true, BlockMain = true };
        await using var runtime = fixture.Runtime(model);
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        var changes = ObserveTitles(runtime);
        await SendAsync(runtime, session.Id, "Investigate independently cancellable model requests");
        await model.TitleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await model.MainStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopped = await runtime.SubmitAsync(new StopCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);
        Assert.True(stopped.Accepted, stopped.RejectionReason);
        await model.MainCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => runtime.GetSessionActivity(session.Id) is { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Cancelled });
        Assert.False(model.TitleCancelled.Task.IsCompleted);
        Assert.False(model.TitleFinished.Task.IsCompleted);

        model.TitleRelease.TrySetResult();
        var generated = await WaitForTitleAsync(fixture.Store, session.Id, SessionTitleSource.Generated);
        await WaitUntilAsync(() => changes.Count == 2);
        Assert.Equal("Generated session title", generated.Title);
        Assert.Single(model.TitleRequests);
        Assert.False(model.TitleCancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task DeleteDuringTitleWrite_DoesNotRecreateSessionOrPublishGeneratedTitle()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new TitleModel { BlockTitle = true };
        var gated = new GatedTitleStore(fixture.Store);
        await using var runtime = fixture.Runtime(model, configure: options => options with { Store = gated });
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var changes = ObserveTitles(runtime);
        await SendAsync(runtime, session.Id, "Keep deleted sessions deleted during background naming");
        await model.TitleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForTurnAsync(projection, 1);
        model.TitleRelease.TrySetResult();
        await gated.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await fixture.Store.DeleteSessionAsync(session.Id, CancellationToken.None);
        }
        finally
        {
            gated.UpdateRelease.TrySetResult();
        }
        await gated.UpdateFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.DisposeAsync();

        Assert.Null(await fixture.Store.GetSessionAsync(session.Id, CancellationToken.None));
        Assert.Empty(await fixture.Store.LoadDurableEventsAsync(session.Id, CancellationToken.None));
        Assert.Single(changes);
    }

    [Fact]
    public async Task Dispose_CancelsTitleAndAwaitsBackgroundCleanupWithoutLateChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new TitleModel { BlockTitle = true, HoldTitleCleanup = true };
        await using var runtime = fixture.Runtime(model);
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var changes = ObserveTitles(runtime);
        await SendAsync(runtime, session.Id, "Investigate title cleanup during session disposal");
        await model.TitleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForTurnAsync(projection, 1);
        var before = await fixture.RecordAsync(session.Id);
        var disposing = runtime.DisposeAsync().AsTask();
        try
        {
            await model.TitleCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(disposing.IsCompleted);
            Assert.False(model.TitleFinished.Task.IsCompleted);
        }
        finally
        {
            model.TitleCleanupRelease.TrySetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(model.TitleFinished.Task.IsCompleted);
        Assert.Equal(before, await fixture.RecordAsync(session.Id));
        Assert.Single(changes);
        Assert.Single(model.TitleRequests);
    }

    [Fact]
    public async Task TitleTimeout_LeavesFallbackWithoutCancellingMainOrRetrying()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = new TitleModel { BlockTitle = true, BlockMain = true };
        await using var runtime = fixture.Runtime(model, configure: options => options with
        {
            SessionTitleGenerationTimeout = TimeSpan.FromMilliseconds(500),
        });
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        const string fallback = "Investigate a title request that exceeds its timeout";
        await SendAsync(runtime, session.Id, fallback);
        await model.TitleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await model.MainStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await model.TitleCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await model.TitleFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(model.MainCancelled.Task.IsCompleted);
        Assert.NotEqual(TurnPhase.Idle, runtime.GetSessionActivity(session.Id)!.Phase);
        model.MainRelease.TrySetResult();
        await WaitForTurnAsync(projection, 1);
        Assert.Equal(TurnOutcome.Completed, runtime.GetSessionActivity(session.Id)!.LastOutcome);
        await SendAsync(runtime, session.Id, "Do not retry title generation after its timeout");
        await WaitForTurnAsync(projection, 2);
        await runtime.DisposeAsync();
        var record = await fixture.RecordAsync(session.Id);
        Assert.Equal(fallback, record.Title);
        Assert.Equal(SessionTitleSource.FirstInput, record.TitleSource);
        Assert.Single(model.TitleRequests);
    }

    private static ConcurrentQueue<(SessionId Id, string Title)> ObserveTitles(AgentRuntime runtime)
    {
        var changes = new ConcurrentQueue<(SessionId, string)>();
        runtime.SessionTitleChanged += (id, title) => changes.Enqueue((id, title));
        return changes;
    }

    private static async Task SendAsync(AgentRuntime runtime, SessionId id, string text, ModelSelection? selection = null)
    {
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), id, 0, text, ModelSelection: selection), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
    }

    private static Task WaitForTurnAsync(IConversationProjection projection, int assistantCount) => WaitUntilAsync(() =>
        projection.Snapshot() is var snapshot && snapshot.State.Phase == TurnPhase.Idle
        && snapshot.Rows.OfType<AssistantMessageRow>().Count(row => !row.IsStreaming) >= assistantCount);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static async Task<SessionRecord> WaitForTitleAsync(SqliteSessionStore store, SessionId id, SessionTitleSource source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var record = await store.GetSessionAsync(id, timeout.Token);
            if (record?.TitleSource == source)
                return record;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitForRejectedTitleAsync(TitleModel model)
    {
        await model.TitleFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Rejected completions have no public background-completion event.
        await Task.Delay(100);
    }

    private sealed class TitleModel(string providerId = "scripted", int? outputLimit = 32_000) : IChatModel
    {
        public ModelDescriptor Descriptor { get; } = new(providerId, "title-model", "Title test model",
            new ModelProperties(ContextWindowTokens: 200_000, DefaultMaxOutputTokens: outputLimit, ReasoningLevels: ["low", "high"]));
        public bool BlockTitle { get; init; }
        public bool BlockMain { get; init; }
        public bool HoldTitleCleanup { get; init; }
        public bool ThrowTitle { get; init; }
        public string TitleText { get; init; } = "{\"title\":\"Generated session title\"}";
        public IReadOnlyList<ModelStreamEvent>? TitleEvents { get; init; }
        public ConcurrentQueue<ModelRequest> TitleRequests { get; } = new();
        public ConcurrentQueue<ModelRequest> MainRequests { get; } = new();
        public TaskCompletionSource TitleStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TitleRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TitleFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TitleCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TitleCleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MainStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MainRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MainCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var isTitle = request.Messages.FirstOrDefault() is { Role: MessageRole.System } system
                && system.Content.OfType<InputTextBlock>().Any(block => block.Text.Contains("title-generation task", StringComparison.Ordinal));
            if (!isTitle)
            {
                MainRequests.Enqueue(request);
                MainStarted.TrySetResult();
                try
                {
                    if (BlockMain)
                        await MainRelease.Task.WaitAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new ModelTextDelta("done");
                    yield return new ModelStreamFinished(ModelFinishReason.Stop);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                        MainCancelled.TrySetResult();
                }
                yield break;
            }

            TitleRequests.Enqueue(request);
            TitleStarted.TrySetResult();
            try
            {
                if (BlockTitle)
                    await TitleRelease.Task.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (ThrowTitle)
                {
                    yield return new ModelTextDelta("partial title before failure");
                    throw new InvalidOperationException("Title generation failed after content");
                }
                foreach (var streamEvent in TitleEvents ?? [new ModelTextDelta(TitleText), new ModelStreamFinished(ModelFinishReason.Stop)])
                    yield return streamEvent;
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    TitleCancelled.TrySetResult();
                    if (HoldTitleCleanup)
                        await TitleCleanupRelease.Task;
                }
                TitleFinished.TrySetResult();
            }
        }
    }

    private sealed class GatedTitleStore(ISessionStore inner) : ISessionStore
    {
        public TaskCompletionSource UpdateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UpdateRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UpdateFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<bool> TryUpdateSessionTitleAsync(SessionId id, string title, SessionTitleSource source, CancellationToken ct)
        {
            if (source != SessionTitleSource.Generated)
                return await inner.TryUpdateSessionTitleAsync(id, title, source, ct);
            UpdateStarted.TrySetResult();
            try
            {
                await UpdateRelease.Task.WaitAsync(ct);
                return await inner.TryUpdateSessionTitleAsync(id, title, source, ct);
            }
            finally
            {
                UpdateFinished.TrySetResult();
            }
        }

        public ValueTask InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public ValueTask UpsertSessionAsync(SessionRecord record, CancellationToken ct) => inner.UpsertSessionAsync(record, ct);
        public ValueTask<SessionRecord?> GetSessionAsync(SessionId id, CancellationToken ct) => inner.GetSessionAsync(id, ct);
        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? root, int limit, CancellationToken ct) => inner.ListSessionsAsync(root, limit, ct);
        public ValueTask DeleteSessionAsync(SessionId id, CancellationToken ct) => inner.DeleteSessionAsync(id, ct);
        public ValueTask AppendMessagesAsync(SessionId id, IReadOnlyList<ConversationMessage> messages, long start, CancellationToken ct) => inner.AppendMessagesAsync(id, messages, start, ct);
        public ValueTask ReplaceMessagesAsync(SessionId id, IReadOnlyList<ConversationMessage> messages, CancellationToken ct) => inner.ReplaceMessagesAsync(id, messages, ct);
        public ValueTask<IReadOnlyList<StoredMessage>> LoadMessagesAsync(SessionId id, CancellationToken ct) => inner.LoadMessagesAsync(id, ct);
        public ValueTask AppendDurableEventsAsync(IReadOnlyList<SequencedEvent> events, CancellationToken ct) => inner.AppendDurableEventsAsync(events, ct);
        public ValueTask<IReadOnlyList<SequencedEvent>> LoadDurableEventsAsync(SessionId id, CancellationToken ct) => inner.LoadDurableEventsAsync(id, ct);
        public ValueTask<long> GetNextMessageSequenceAsync(SessionId id, CancellationToken ct) => inner.GetNextMessageSequenceAsync(id, ct);
    }

    private sealed class Fixture(string root, SqliteSessionStore store) : IAsyncDisposable
    {
        public string Root { get; } = root;
        public string DatabasePath => Path.Combine(Root, "sessions.db");
        public SqliteSessionStore Store { get; } = store;

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "alcoler-title-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new SqliteSessionStore(Path.Combine(root, "sessions.db"));
            await store.InitializeAsync(CancellationToken.None);
            return new Fixture(root, store);
        }

        public AgentRuntime Runtime(TitleModel model, bool? enabled = true,
            Func<AgentRuntimeOptions, AgentRuntimeOptions>? configure = null, SqliteSessionStore? store = null)
        {
            var artifacts = new FileArtifactStore(Path.Combine(Root, "artifacts"));
            var options = new AgentRuntimeOptions
            {
                Store = store ?? Store,
                Artifacts = artifacts,
                Providers = [new ScriptedModelProvider(model, model.Descriptor.ProviderId)],
                Tools = [],
                WorkspaceFactory = workspaceRoot => new WorkspaceToolEnvironment(workspaceRoot, artifacts),
                DefaultModel = new ModelSelection(model.Descriptor.ProviderId, model.Descriptor.ModelId),
            };
            if (enabled is { } value)
                options = options with { SessionTitleGenerationEnabled = value };
            return new AgentRuntime(configure?.Invoke(options) ?? options);
        }

        public async Task<SessionRecord> RecordAsync(SessionId id) =>
            Assert.IsType<SessionRecord>(await Store.GetSessionAsync(id, CancellationToken.None));

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
