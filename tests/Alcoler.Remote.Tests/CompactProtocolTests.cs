using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Alcoler.Remote.Tests;

[Collection("stream-gateway")]
public sealed class CompactProtocolTests(GatewayHarness harness, ITestOutputHelper output)
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private const string InputSecret = "HIDDEN_INPUT_PAYLOAD";
    private const string OutputSecret = "HIDDEN_TOOL_LOG";
    private const string ReasoningSecret = "HIDDEN_REASONING";

    [Fact]
    public async Task CompactSnapshot_IsMuchSmaller_AndExpansionDefersFullToolPayloads()
    {
        var (session, token) = await harness.NewSessionAsync();
        var input = JsonSerializer.SerializeToElement(new
        {
            subagent_type = "explorer",
            file_path = new string('p', 1024) + "/actual-filename.cs",
            description = new string('d', 1000),
            command = "dotnet test",
            content = InputSecret + new string('i', 700_000),
            nested = new { secret = InputSecret },
        });
        var tool = Tool("tool", input, OutputSecret + new string('o', 2_000_000));
        var process = Assistant("process",
            new ReasoningPart(new PartId("process-reasoning"), ReasoningSecret + new string('r', 400_000)),
            new ToolCallPart(new PartId("call"), tool.Id, tool.ToolName, input),
            new ToolResultPart(new PartId("result"), tool.Id, tool.Output!));
        var answer = Assistant("answer", new TextPart(new PartId("text"), "The final answer"),
            new ReasoningPart(new PartId("final-reasoning"), ReasoningSecret + " final", ProviderSignature: "private-signature"),
            new FilePart(new PartId("file"), "alcoler-artifact://session/file", "image/png", "preview.png"),
            new ToolCallPart(new PartId("final-call"), tool.Id, tool.ToolName, input),
            new ToolResultPart(new PartId("final-result"), tool.Id, tool.Output!),
            new AttachmentPart(new PartId("final-attachment"), AttachmentKind.SystemReminder, InputSecret)) with
        { CanFork = true };
        ConversationRow[] rows =
        [
            User("user"), process, tool, answer,
            new SystemNoticeRow("tail-notice", "info", "A trailing notice"),
            new FileChangeSummaryRow("tail-files", [new FileChangeSummaryItem("changed.cs", 2, 1)], 2, 1),
        ];
        harness.Runtime.SetTranscript(session, rows, State());
        using var legacy = await Connect(session, token, "");
        using var compact = await Connect(session, token);
        var legacyText = await ReceiveText(legacy);
        var compactText = await ReceiveText(compact);
        var legacyBytes = Encoding.UTF8.GetByteCount(legacyText);
        var compactBytes = Encoding.UTF8.GetByteCount(compactText);
        output.WriteLine($"Snapshot wire bytes: legacy={legacyBytes:N0}; compact={compactBytes:N0}; reduction={(1d - (double)compactBytes / legacyBytes):P3}");
        Assert.True(compactBytes < legacyBytes / 100, $"Compact {compactBytes} bytes versus legacy {legacyBytes}.");
        Assert.Contains(OutputSecret, legacyText);
        AssertNoHiddenPayload(compactText);
        using var snapshot = JsonDocument.Parse(compactText);
        var visible = snapshot.RootElement.GetProperty("snapshot").GetProperty("rows");
        Assert.Equal(new[] { "user", "tail-notice", "tail-files" }, Ids(visible));
        var summary = visible[0];
        Assert.Equal("turn_summary", Type(summary));
        Assert.Equal("user_message", Type(summary.GetProperty("user")));
        Assert.True(summary.GetProperty("user").GetProperty("canRewind").GetBoolean());
        Assert.Equal("answer", summary.GetProperty("answer").GetProperty("rowId").GetString());
        Assert.True(summary.GetProperty("answer").GetProperty("canFork").GetBoolean());
        Assert.Equal(new[] { "text", "file" }, summary.GetProperty("answer").GetProperty("parts").EnumerateArray().Select(Type));
        Assert.Equal("00:00:03", summary.GetProperty("duration").GetString());
        Assert.Equal(3, summary.GetProperty("detailCount").GetInt32());
        var version = summary.GetProperty("detailVersion").GetString()!;
        using var detailsResponse = await Get(session, "turns/user", token, version);
        Assert.Equal(HttpStatusCode.OK, detailsResponse.StatusCode);
        var detailsText = await detailsResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(InputSecret, detailsText);
        Assert.DoesNotContain(OutputSecret, detailsText);
        Assert.Contains(ReasoningSecret, detailsText);
        using var details = JsonDocument.Parse(detailsText);
        Assert.Equal(version, details.RootElement.GetProperty("version").GetString());
        Assert.Equal(0, details.RootElement.GetProperty("sequence").GetInt64());
        var detailRows = details.RootElement.GetProperty("rows");
        Assert.Equal(new[] { "process", "tool", "answer" }, Ids(detailRows));
        var header = detailRows[1];
        AssertToolHeader(header);
        var boundedInput = header.GetProperty("input");
        Assert.Equal("explorer", boundedInput.GetProperty("subagent_type").GetString());
        Assert.EndsWith("/actual-filename.cs", boundedInput.GetProperty("file_path").GetString());
        Assert.StartsWith("...", boundedInput.GetProperty("file_path").GetString());
        Assert.True(boundedInput.EnumerateObject().Count() <= 8);
        Assert.All(boundedInput.EnumerateObject(), property => Assert.True(property.Value.GetString()!.Length <= 256));
        Assert.All(detailRows[2].GetProperty("parts").EnumerateArray(), part => Assert.Equal("reasoning", Type(part)));
        var toolVersion = header.GetProperty("detailVersion").GetString()!;
        using var fullResponse = await Get(session, "rows/tool", token, toolVersion);
        Assert.Equal(HttpStatusCode.OK, fullResponse.StatusCode);
        using var full = await fullResponse.ReadJsonAsync();
        Assert.Equal(toolVersion, full.RootElement.GetProperty("version").GetString());
        Assert.Equal(GatewayHarness.Serialize<ConversationRow>(tool), full.RootElement.GetProperty("row").GetRawText());
        Assert.DoesNotContain("detailsDeferred", full.RootElement.GetProperty("row").GetRawText());
        using var reconnect = await Connect(session, token);
        using var reconnected = JsonDocument.Parse(await ReceiveText(reconnect));
        Assert.Equal(version, reconnected.RootElement.GetProperty("snapshot").GetProperty("rows")[0].GetProperty("detailVersion").GetString());
        using var repeated = await Get(session, "turns/user", token, version);
        using var repeatedDetails = await repeated.ReadJsonAsync();
        Assert.Equal(toolVersion, repeatedDetails.RootElement.GetProperty("rows")[1].GetProperty("detailVersion").GetString());
        legacy.Abort();
        compact.Abort();
        reconnect.Abort();
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Browser")]
    public async Task CompactImageTools_ExposePreviewsInSnapshotsDeltasAndTurnDetails_WithoutOpeningCompletedTurns(string toolName)
    {
        var (session, token) = await harness.NewSessionAsync();
        var image = new ToolResultMedia("alcoler-artifact://session/preview.png", "image/png", "preview.png");
        ToolResultBlock[] blocks =
        [
            new ToolResultText(OutputSecret + new string('o', 1_000_000)), image,
            new ToolResultMedia("alcoler-artifact://session/audio.mp3", "audio/mpeg", "audio.mp3"),
        ];
        var tool = Tool("image-tool", JsonSerializer.SerializeToElement(new
        {
            path = "preview.png",
            content = InputSecret + new string('i', 500_000),
        })) with
        { ToolName = toolName, Output = blocks };
        var streaming = Assistant("answer", new TextPart(new PartId("answer-text"), "streaming"),
            new ToolResultPart(new PartId("hidden-result"), tool.Id, blocks)) with
        { IsStreaming = true };
        harness.Runtime.SetTranscript(session, [User("user"), tool, streaming],
            State() with { Phase = TurnPhase.Streaming, ActiveTurn = new TurnId("active") });
        using var socket = await Connect(session, token);
        var snapshotText = await ReceiveText(socket);
        AssertNoHiddenPayload(snapshotText);
        using var snapshot = JsonDocument.Parse(snapshotText);
        var rows = snapshot.RootElement.GetProperty("snapshot").GetProperty("rows");
        Assert.Equal(new[] { "user", "image-tool", "answer" }, Ids(rows));
        AssertToolHeader(rows[1]);
        AssertPreview(Assert.Single(rows[1].GetProperty("imagePreviews").EnumerateArray()), image);
        Assert.False(rows[1].TryGetProperty("imagePreviewsTruncated", out _));
        Assert.DoesNotContain("audio.mp3", snapshotText);
        var updatedImage = image with { ArtifactUri = "alcoler-artifact://session/updated.webp", MimeType = "image/webp", FileName = "updated.webp" };
        var updatedTool = tool with { Output = [blocks[0], updatedImage] };
        harness.Runtime.Publish(session, new RowUpserted(updatedTool));
        var deltaText = await ReceiveText(socket);
        AssertNoHiddenPayload(deltaText);
        using var delta = JsonDocument.Parse(deltaText);
        var header = Assert.Single(delta.RootElement.GetProperty("delta").GetProperty("ops").EnumerateArray()).GetProperty("row");
        AssertToolHeader(header);
        AssertPreview(Assert.Single(header.GetProperty("imagePreviews").EnumerateArray()), updatedImage);
        harness.Runtime.Publish(session, new RowUpserted(Assistant("answer", new TextPart(new PartId("answer-text"), "finished"))),
            new StatePatched(State()));
        var completionText = await ReceiveText(socket);
        AssertNoHiddenPayload(completionText);
        Assert.DoesNotContain("imagePreviews", completionText);
        Assert.DoesNotContain(updatedImage.ArtifactUri, completionText);
        using var completion = JsonDocument.Parse(completionText);
        var summary = Assert.Single(completion.RootElement.GetProperty("delta").GetProperty("ops").EnumerateArray(),
            op => Type(op) == "row_upserted").GetProperty("row");
        Assert.Equal("turn_summary", Type(summary));
        Assert.Equal("user", summary.GetProperty("rowId").GetString());
        using var detailsResponse = await Get(session, "turns/user", token, summary.GetProperty("detailVersion").GetString());
        Assert.Equal(HttpStatusCode.OK, detailsResponse.StatusCode);
        var detailsText = await detailsResponse.Content.ReadAsStringAsync();
        AssertNoHiddenPayload(detailsText);
        using var details = JsonDocument.Parse(detailsText);
        var child = Assert.Single(details.RootElement.GetProperty("rows").EnumerateArray());
        AssertToolHeader(child);
        AssertPreview(Assert.Single(child.GetProperty("imagePreviews").EnumerateArray()), updatedImage);
        using var reconnect = await Connect(session, token);
        var foldedText = await ReceiveText(reconnect);
        Assert.DoesNotContain("imagePreviews", foldedText);
        Assert.DoesNotContain(updatedImage.ArtifactUri, foldedText);
        using var folded = JsonDocument.Parse(foldedText);
        Assert.Equal("turn_summary", Type(Assert.Single(folded.RootElement.GetProperty("snapshot").GetProperty("rows").EnumerateArray())));
        reconnect.Abort();
        socket.Abort();
    }

    [Fact]
    public void CompactImagePreviews_NormalizeSafeMimeTypes_Deduplicate_AndBoundFileNames()
    {
        var longFileName = new string('f', CompactImagePreviews.MaxFileNameLength - 4) + "\U0001F600" + new string('f', 5000);
        var uri = "alcoler-artifact://session/" + new string('p', CompactImagePreviews.MaxUriLength - "alcoler-artifact://session/".Length);
        ToolResultMedia[] images =
        [
            new(uri, "IMAGE/PNG", longFileName),
            new("alcoler-artifact://session/photo.jpeg", "Image/Jpeg"),
            new("ALCOLER-ARTIFACT://session/animation.gif", "image/GIF", "animation.gif"),
            new("alcoler-artifact://session/photo.webp", "IMAGE/WEBP", "photo.webp"),
        ];
        var tool = Tool("images") with { Output = [.. images, images[0] with { MimeType = "image/png" }] };
        using var snapshot = ToolSnapshot(tool);
        var header = snapshot.RootElement.GetProperty("snapshot").GetProperty("rows")[0];
        AssertToolHeader(header);
        var previews = header.GetProperty("imagePreviews");
        Assert.Equal(4, previews.GetArrayLength());
        Assert.Equal(new[] { "image/png", "image/jpeg", "image/gif", "image/webp" },
            previews.EnumerateArray().Select(preview => preview.GetProperty("mimeType").GetString()));
        Assert.Equal(uri, previews[0].GetProperty("artifactUri").GetString());
        Assert.Equal(new string('f', CompactImagePreviews.MaxFileNameLength - 4) + "...", previews[0].GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Null, previews[1].GetProperty("fileName").ValueKind);
        Assert.False(header.TryGetProperty("imagePreviewsTruncated", out _));
        Assert.All(previews.EnumerateArray(), preview => Assert.Equal(new[] { "artifactUri", "mimeType", "fileName" },
            preview.EnumerateObject().Select(property => property.Name)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactImagePreviews_CapUniqueDescriptors_AndFlagOversizedUrisWithoutTruncatingThem(bool oversizedUri)
    {
        var oversized = "alcoler-artifact://session/" + new string('x', CompactImagePreviews.MaxUriLength);
        var images = Enumerable.Range(0, oversizedUri ? CompactImagePreviews.MaxCount : CompactImagePreviews.MaxCount + 1)
            .Select(index => new ToolResultMedia($"alcoler-artifact://session/image-{index}.png", "image/png", $"image-{index}.png")).ToArray();
        var tool = Tool("images") with
        {
            Output = oversizedUri ? [new ToolResultMedia(oversized, "image/png"), .. images] : images,
        };
        using var snapshot = ToolSnapshot(tool);
        var header = snapshot.RootElement.GetProperty("snapshot").GetProperty("rows")[0];
        AssertToolHeader(header);
        Assert.Equal(images.Take(CompactImagePreviews.MaxCount).Select(image => image.ArtifactUri),
            header.GetProperty("imagePreviews").EnumerateArray().Select(preview => preview.GetProperty("artifactUri").GetString()));
        Assert.True(header.GetProperty("imagePreviewsTruncated").GetBoolean());
        Assert.DoesNotContain(new string('x', 100), header.GetRawText());
    }

    [Theory]
    [InlineData("IMAGE/PNG; charset=binary")]
    [InlineData("  IMAGE/PNG  ; charset=binary  ")]
    [InlineData("\timage/png\r\n")]
    public void CompactImagePreviews_AcceptSafeMimeEssenceWithParametersAndWhitespace(string mimeType)
    {
        var image = new ToolResultMedia("alcoler-artifact://session/image.png", mimeType, "image.png");
        using var snapshot = ToolSnapshot(Tool("image") with { Output = [image] });
        var header = snapshot.RootElement.GetProperty("snapshot").GetProperty("rows")[0];
        AssertToolHeader(header);
        AssertPreview(Assert.Single(header.GetProperty("imagePreviews").EnumerateArray()), image with { MimeType = "image/png" });
        Assert.False(header.TryGetProperty("imagePreviewsTruncated", out _));
    }

    [Theory]
    [InlineData("https://example.test/image.png", "image/png", true)]
    [InlineData("file:///C:/image.png", "image/png", true)]
    [InlineData("data:image/png;base64,aGVsbG8=", "image/png", true)]
    [InlineData("javascript:alert(1)", "image/png", true)]
    [InlineData("alcoler-artifact://", "image/png", true)]
    [InlineData("alcoler-artifact://session/image.svg", "image/svg+xml", true)]
    [InlineData("alcoler-artifact://session/image.unknown", " IMAGE/UNKNOWN; encoding=binary ", true)]
    [InlineData("alcoler-artifact://session/audio.mp3", "audio/mpeg", false)]
    [InlineData("alcoler-artifact://session/document.pdf", "application/pdf", false)]
    public void CompactImagePreviews_ExcludeUnsafeAndNonImageMedia(string uri, string mimeType, bool truncated)
    {
        using var snapshot = ToolSnapshot(Tool("image") with { Output = [new ToolResultMedia(uri, mimeType)] });
        var header = snapshot.RootElement.GetProperty("snapshot").GetProperty("rows")[0];
        AssertToolHeader(header);
        Assert.False(header.TryGetProperty("imagePreviews", out _));
        Assert.Equal(truncated, header.TryGetProperty("imagePreviewsTruncated", out var flag));
        if (truncated) Assert.True(flag.GetBoolean());
        Assert.DoesNotContain(uri, header.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactLiveTurn_PreservesPermissionAndStreaming_ThenFoldsAtSeal(bool appendText)
    {
        var (session, token) = await harness.NewSessionAsync();
        var oldTool = Tool("old-tool");
        var liveTool = Tool("live-tool", output: OutputSecret) with { Status = ToolCallStatus.AwaitingPermission };
        var streaming = Assistant("live-answer", new TextPart(new PartId("live-text"), "baseline")) with { IsStreaming = true };
        var permission = new PermissionRequest(new InteractionId("approval"), session, "Bash",
            JsonSerializer.SerializeToElement(new { command = "must remain available for consent" }), RiskLevel.Medium,
            "Approval is required", ToolCallId: liveTool.Id);
        var active = State() with { Phase = TurnPhase.AwaitingPermission, ActiveTurn = new TurnId("live"), PendingPermission = permission };
        harness.Runtime.SetTranscript(session,
        [
            User("old-user"), oldTool, Assistant("old-answer", new TextPart(new PartId("old-text"), "old answer")),
            User("live-user"), liveTool, streaming,
        ], active);
        using var socket = await Connect(session, token, "compact=1" + (appendText ? "&appendText=1" : ""));
        using var initial = JsonDocument.Parse(await ReceiveText(socket));
        var snapshot = initial.RootElement.GetProperty("snapshot");
        var initialRows = snapshot.GetProperty("rows");
        var clientRows = initialRows.EnumerateArray().Select(row => row.Clone()).ToList();
        Assert.Equal(new[] { "old-user", "live-user", "live-tool", "live-answer" }, Ids(initialRows));
        var oldVersion = initialRows[0].GetProperty("detailVersion").GetString();
        Assert.Equal("user_message", Type(initialRows[1]));
        AssertToolHeader(initialRows[2]);
        Assert.Equal("Approval is required", snapshot.GetProperty("state").GetProperty("pendingPermission").GetProperty("reason").GetString());
        Assert.Equal("must remain available for consent", snapshot.GetProperty("state").GetProperty("pendingPermission").GetProperty("input").GetProperty("command").GetString());
        using var unavailable = await Get(session, "turns/live-user", token);
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        var running = active with { Phase = TurnPhase.Streaming, PendingPermission = null };
        var chunk = harness.Runtime.Publish(session, new RowTextAppended("live-answer", " chunk", At.AddSeconds(4)), new StatePatched(running));
        using var chunkFrame = JsonDocument.Parse(await ReceiveText(socket));
        var chunkDelta = chunkFrame.RootElement.GetProperty("delta");
        Assert.Equal(chunk.Sequence, chunkDelta.GetProperty("sequence").GetInt64());
        var chunkOp = Assert.Single(chunkDelta.GetProperty("ops").EnumerateArray(), op => Type(op) != "state_patch");
        if (appendText)
        {
            Assert.Equal("row_text_appended", Type(chunkOp));
            Assert.Equal(" chunk", chunkOp.GetProperty("delta").GetString());
        }
        else
        {
            Assert.Equal("row_upserted", Type(chunkOp));
            Assert.Equal("baseline chunk", chunkOp.GetProperty("row").GetProperty("parts")[0].GetProperty("text").GetString());
        }
        Apply(clientRows, chunkDelta.GetProperty("ops"));
        var answer = streaming with
        {
            IsStreaming = false,
            At = At.AddSeconds(5),
            Parts = [new TextPart(new PartId("final-text"), "finished"), new ReasoningPart(new PartId("final-thought"), ReasoningSecret)],
        };
        var completed = harness.Runtime.Publish(session,
            new RowUpserted(liveTool with { Status = ToolCallStatus.Succeeded }), new RowUpserted(answer),
            new RowAppended(new SystemNoticeRow("tail", "info", "after the answer")),
            new RowAppended(new FileChangeSummaryRow("files", [], 0, 0)), new StatePatched(State()));
        var completedText = await ReceiveText(socket);
        AssertNoHiddenPayload(completedText);
        using var completion = JsonDocument.Parse(completedText);
        var delta = completion.RootElement.GetProperty("delta");
        Assert.Equal(completed.Sequence, delta.GetProperty("sequence").GetInt64());
        var ops = delta.GetProperty("ops");
        var fold = Assert.Single(ops.EnumerateArray(), op => Type(op) == "row_upserted");
        Assert.Equal("turn_summary", Type(fold.GetProperty("row")));
        Assert.Equal("live-user", fold.GetProperty("row").GetProperty("rowId").GetString());
        Assert.Equal(new[] { "live-tool", "live-answer" }, ops.EnumerateArray().Where(op => Type(op) == "row_removed").Select(op => op.GetProperty("rowId").GetString()));
        Apply(clientRows, ops);
        Assert.Equal(new[] { "old-user", "live-user", "tail", "files" }, clientRows.Select(row => row.GetProperty("rowId").GetString()));
        Assert.Equal(oldVersion, clientRows[0].GetProperty("detailVersion").GetString());
        Assert.Equal("finished", clientRows[1].GetProperty("answer").GetProperty("parts")[0].GetProperty("text").GetString());
        using var detailResponse = await Get(session, "turns/live-user", token, clientRows[1].GetProperty("detailVersion").GetString());
        using var detail = await detailResponse.ReadJsonAsync();
        Assert.Equal(new[] { "live-tool", "live-answer" }, Ids(detail.RootElement.GetProperty("rows")));
        socket.Abort();
    }

    [Fact]
    public async Task CompactChanges_InvalidateStaleDetails_AndRewindRestoresCorrectOrder()
    {
        var (session, token) = await harness.NewSessionAsync();
        var firstTool = Tool("tool-1");
        var secondTool = Tool("tool-2");
        harness.Runtime.SetTranscript(session,
        [
            User("user-1"), firstTool, Assistant("answer-1", new TextPart(new PartId("text-1"), "first")),
            new SystemNoticeRow("tail-1", "info", "tail"),
            User("user-2"), secondTool, Assistant("answer-2", new TextPart(new PartId("text-2"), "second")),
        ], State());
        using var socket = await Connect(session, token);
        using var initial = JsonDocument.Parse(await ReceiveText(socket));
        var rows = initial.RootElement.GetProperty("snapshot").GetProperty("rows").EnumerateArray().Select(row => row.Clone()).ToList();
        var firstVersion = rows[0].GetProperty("detailVersion").GetString()!;
        var secondVersion = rows[2].GetProperty("detailVersion").GetString();
        using var firstDetailsResponse = await Get(session, "turns/user-1", token, firstVersion);
        using var firstDetails = await firstDetailsResponse.ReadJsonAsync();
        var firstToolVersion = firstDetails.RootElement.GetProperty("rows")[0].GetProperty("detailVersion").GetString();
        var changed = harness.Runtime.Publish(session, new RowUpserted(firstTool with { Output = [new ToolResultText("changed authoritative output")] }));
        using var changedFrame = JsonDocument.Parse(await ReceiveText(socket));
        Apply(rows, changedFrame.RootElement.GetProperty("delta").GetProperty("ops"));
        var currentVersion = rows[0].GetProperty("detailVersion").GetString();
        Assert.NotEqual(firstVersion, currentVersion);
        Assert.Equal(secondVersion, rows[2].GetProperty("detailVersion").GetString());
        using var staleTurnResponse = await Get(session, "turns/user-1", token, firstVersion);
        Assert.Equal(HttpStatusCode.Conflict, staleTurnResponse.StatusCode);
        using var staleTurn = await staleTurnResponse.ReadJsonAsync();
        Assert.Equal("stale_details", staleTurn.RootElement.GetProperty("error").GetString());
        Assert.Equal(currentVersion, staleTurn.RootElement.GetProperty("version").GetString());
        Assert.Equal(changed.Sequence, staleTurn.RootElement.GetProperty("sequence").GetInt64());
        using var staleToolResponse = await Get(session, "rows/tool-1", token, firstToolVersion);
        Assert.Equal(HttpStatusCode.Conflict, staleToolResponse.StatusCode);
        using var staleTool = await staleToolResponse.ReadJsonAsync();
        Assert.False(staleTool.RootElement.TryGetProperty("row", out _));
        Assert.NotEqual(firstToolVersion, staleTool.RootElement.GetProperty("version").GetString());
        harness.Runtime.Publish(session, new RowRemoved("answer-1"));
        using var unfolded = JsonDocument.Parse(await ReceiveText(socket));
        Apply(rows, unfolded.RootElement.GetProperty("delta").GetProperty("ops"));
        Assert.Equal(new[] { "user-1", "tool-1", "tail-1", "user-2" }, rows.Select(row => row.GetProperty("rowId").GetString()));
        Assert.Equal("user_message", Type(rows[0]));
        AssertToolHeader(rows[1]);
        Assert.Equal(secondVersion, rows[3].GetProperty("detailVersion").GetString());
        using var removedTurn = await Get(session, "turns/user-1", token, currentVersion);
        Assert.Equal(HttpStatusCode.NotFound, removedTurn.StatusCode);
        harness.Runtime.Publish(session, new RowRemoved("answer-2"), new RowRemoved("tool-2"), new RowRemoved("user-2"));
        using var rewound = JsonDocument.Parse(await ReceiveText(socket));
        Apply(rows, rewound.RootElement.GetProperty("delta").GetProperty("ops"));
        Assert.Equal(new[] { "user-1", "tool-1", "tail-1" }, rows.Select(row => row.GetProperty("rowId").GetString()));
        using var removedRow = await Get(session, "rows/tool-2", token);
        using var removedAnchor = await Get(session, "turns/user-2", token);
        Assert.Equal(HttpStatusCode.NotFound, removedRow.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removedAnchor.StatusCode);
        socket.Abort();
    }

    [Theory]
    [InlineData(ToolCallStatus.Pending)]
    [InlineData(ToolCallStatus.Running)]
    [InlineData(ToolCallStatus.AwaitingPermission)]
    [InlineData(ToolCallStatus.Failed)]
    [InlineData(ToolCallStatus.Cancelled)]
    public async Task CompactSnapshot_DoesNotHideImportantToolState(ToolCallStatus status)
    {
        var (session, token) = await harness.NewSessionAsync();
        harness.Runtime.SetTranscript(session,
            [User("user"), Tool("tool") with { Status = status }, Assistant("answer", new TextPart(new PartId("text"), "answer"))], State());
        using var socket = await Connect(session, token);
        using var snapshot = JsonDocument.Parse(await ReceiveText(socket));
        var rows = snapshot.RootElement.GetProperty("snapshot").GetProperty("rows");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("user_message", Type(rows[0]));
        Assert.Equal(status.ToString(), rows[1].GetProperty("status").GetString());
        AssertToolHeader(rows[1]);
        socket.Abort();
    }

    [Theory]
    [InlineData("turn_error")]
    [InlineData("running_subagent")]
    public async Task CompactSnapshot_DoesNotHideErrorsOrRunningSubagents(string kind)
    {
        var (session, token) = await harness.NewSessionAsync();
        var agent = kind == "running_subagent"
            ? new SubagentState(new SubagentId("agent"), new SessionId("child"), "still running", SubagentStatus.Running, null, At, null)
            : null;
        harness.Runtime.SetTranscript(session,
            [User("user"), new SystemNoticeRow("important", kind, "important status", agent), Assistant("answer", new TextPart(new PartId("text"), "answer"))], State());
        using var socket = await Connect(session, token);
        using var snapshot = JsonDocument.Parse(await ReceiveText(socket));
        var rows = snapshot.RootElement.GetProperty("snapshot").GetProperty("rows");
        Assert.Equal(new[] { "user", "important", "answer" }, Ids(rows));
        Assert.Equal("important status", rows[1].GetProperty("text").GetString());
        socket.Abort();
    }

    [Theory]
    [InlineData("")]
    [InlineData("compact=0")]
    public async Task LegacySnapshot_WithoutOptIn_RemainsByteIdentical(string query)
    {
        var (session, token) = await harness.NewSessionAsync();
        ConversationRow[] rows = [User("user"), Tool("tool", output: OutputSecret), Assistant("answer", new TextPart(new PartId("text"), "answer"), new ReasoningPart(new PartId("reason"), ReasoningSecret))];
        var state = State();
        harness.Runtime.SetTranscript(session, rows, state);
        using var socket = await Connect(session, token, query);
        Assert.Equal(Encoding.UTF8.GetString(GatewayJson.SnapshotFrame(new ConversationSnapshot(0, 0, rows, state))), await ReceiveText(socket));
        var updated = Tool("tool", output: "full legacy update");
        var delta = harness.Runtime.Publish(session, new RowUpserted(updated));
        Assert.Equal(Encoding.UTF8.GetString(GatewayJson.DeltaFrame(delta)), await ReceiveText(socket));
        socket.Abort();
    }

    [Fact]
    public async Task DetailEndpoints_EnforceAuthAndSessionScope_AndDisposeObservers()
    {
        var (session, token) = await harness.NewSessionAsync();
        var other = await harness.CreateSessionAsync(token);
        harness.Runtime.SetTranscript(session, [User("user"), Tool("tool", output: "session one"), Assistant("answer", new TextPart(new PartId("text"), "one"))], State());
        harness.Runtime.SetTranscript(other, [Tool("tool", output: "session two")], State());
        foreach (var path in new[] { "turns/user", "rows/tool" })
        {
            using var denied = await harness.Client.GetAsync($"api/sessions/{session.Value}/{path}");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var missingSession = await harness.GetWithTokenAsync($"api/sessions/sess_missing/{path}", token);
            Assert.Equal(HttpStatusCode.NotFound, missingSession.StatusCode);
            using var malformed = await harness.GetWithTokenAsync($"api/sessions/{session.Value}/{path}?version=a&version=b", token);
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }
        Assert.Equal(0, harness.Runtime.ObserverCount(session));
        for (var index = 0; index < 3; index++)
        {
            using var first = await Get(session, "turns/user", token);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var second = await Get(other, "rows/tool", token);
            var body = await second.Content.ReadAsStringAsync();
            Assert.Contains("session two", body);
            Assert.DoesNotContain("session one", body);
            Assert.Equal(0, harness.Runtime.ObserverCount(session));
            Assert.Equal(0, harness.Runtime.ObserverCount(other));
        }
        using var wrongTurn = await Get(other, "turns/user", token);
        using var missingRow = await Get(session, "rows/missing", token);
        Assert.Equal(HttpStatusCode.NotFound, wrongTurn.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRow.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetailEndpoints_ReuseActiveStreams_AndFallBackAfterLastClose(bool compact)
    {
        var (session, token) = await harness.NewSessionAsync();
        harness.Runtime.SetTranscript(session,
            [User("user"), Tool("tool"), Assistant("answer", new TextPart(new PartId("text"), "answer"))], State());
        using var first = await Connect(session, token, compact ? "compact=1" : "");
        using var firstSnapshot = JsonDocument.Parse(await ReceiveText(first));
        Assert.Equal(1, harness.Runtime.ProjectionCount(session));
        for (var index = 0; index < 8; index++)
        {
            using var turn = await Get(session, "turns/user", token);
            using var row = await Get(session, "rows/tool", token);
            Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
            Assert.Equal(HttpStatusCode.OK, row.StatusCode);
        }
        Assert.Equal(1, harness.Runtime.ProjectionCount(session));
        Assert.Equal(1, harness.Runtime.ObserverCount(session));
        using var second = await Connect(session, token);
        using var secondSnapshot = JsonDocument.Parse(await ReceiveText(second));
        Assert.Equal(2, harness.Runtime.ProjectionCount(session));
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "first closed", CancellationToken.None).WaitAsync(Budget);
        await WaitForObservers(session, 1);
        using var surviving = await Get(session, "turns/user", token);
        Assert.Equal(HttpStatusCode.OK, surviving.StatusCode);
        Assert.Equal(2, harness.Runtime.ProjectionCount(session));
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "last closed", CancellationToken.None).WaitAsync(Budget);
        await WaitForObservers(session, 0);
        using var fallbackTurn = await Get(session, "turns/user", token);
        using var fallbackRow = await Get(session, "rows/tool", token);
        Assert.Equal(HttpStatusCode.OK, fallbackTurn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fallbackRow.StatusCode);
        Assert.Equal(4, harness.Runtime.ProjectionCount(session));
        Assert.Equal(0, harness.Runtime.ObserverCount(session));
    }

    [Theory]
    [InlineData("turns/user")]
    [InlineData("rows/tool")]
    public async Task DetailCapture_DelaysStreamDisposalUntilSnapshotIsDetached(string path)
    {
        var (session, token) = await harness.NewSessionAsync();
        harness.Runtime.SetTranscript(session,
            [User("user"), Tool("tool"), Assistant("answer", new TextPart(new PartId("text"), "answer"))], State());
        using var projection = new ClosingProjection(harness.Runtime.Subscribe(session));
        harness.Runtime.UseProjection(session, () => projection);
        using var socket = await Connect(session, token);
        using var initial = JsonDocument.Parse(await ReceiveText(socket));
        var count = harness.Runtime.ProjectionCount(session);
        projection.BlockNextSnapshot = true;
        var responseTask = Get(session, path, token);
        try
        {
            await projection.CaptureStarted.Task.WaitAsync(Budget);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "close during capture", CancellationToken.None).WaitAsync(Budget);
            Assert.False(projection.Disposed.Task.IsCompleted);
            projection.Release.TrySetResult();
            using var response = await responseTask.WaitAsync(Budget);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await response.ReadJsonAsync();
            Assert.True(body.RootElement.TryGetProperty("version", out _));
            await projection.Disposed.Task.WaitAsync(Budget);
            Assert.False(projection.DisposedDuringCapture);
            Assert.Equal(count, harness.Runtime.ProjectionCount(session));
            harness.Runtime.UseProjection(session, null);
            using var fallback = await Get(session, path, token);
            Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
            Assert.Equal(count + 1, harness.Runtime.ProjectionCount(session));
            Assert.Equal(0, harness.Runtime.ObserverCount(session));
        }
        finally
        {
            projection.Release.TrySetResult();
            harness.Runtime.UseProjection(session, null);
            socket.Abort();
        }
    }

    [Theory]
    [InlineData("turns/user")]
    [InlineData("rows/tool")]
    public async Task DelayedDetails_KeepTheirCapturedVersionAndSequence_WhenNewerSnapshotArrives(string path)
    {
        var (session, token) = await harness.NewSessionAsync();
        var tool = Tool("tool", output: "old detail");
        harness.Runtime.SetTranscript(session,
            [User("user"), tool, Assistant("answer", new TextPart(new PartId("text"), "answer"))], State());
        using var socket = await Connect(session, token);
        using var initial = JsonDocument.Parse(await ReceiveText(socket));
        var initialVersion = path.StartsWith("turns", StringComparison.Ordinal)
            ? initial.RootElement.GetProperty("snapshot").GetProperty("rows")[0].GetProperty("detailVersion").GetString()!
            : CompactContentVersion.ForRow(tool);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "switch to fallback", CancellationToken.None).WaitAsync(Budget);
        await WaitForObservers(session, 0);
        ConversationSnapshot captured;
        using (var observer = harness.Runtime.Subscribe(session)) captured = observer.Snapshot();
        using var delayed = new CapturedProjection(captured);
        harness.Runtime.UseProjection(session, () => delayed);
        var responseTask = Get(session, path, token, initialVersion);
        try
        {
            await delayed.Requested.Task.WaitAsync(Budget);
            harness.Runtime.UseProjection(session, null);
            using var liveSocket = await Connect(session, token);
            using var liveSnapshot = JsonDocument.Parse(await ReceiveText(liveSocket));
            var updatedTool = tool with { Output = [new ToolResultText("newer detail")] };
            var changed = harness.Runtime.Publish(session, new RowUpserted(updatedTool));
            using var liveDelta = JsonDocument.Parse(await ReceiveText(liveSocket));
            Assert.Equal(changed.Sequence, liveDelta.RootElement.GetProperty("delta").GetProperty("sequence").GetInt64());
            using var reconnect = await Connect(session, token);
            using var newerSnapshot = JsonDocument.Parse(await ReceiveText(reconnect));
            var currentVersion = path.StartsWith("turns", StringComparison.Ordinal)
                ? newerSnapshot.RootElement.GetProperty("snapshot").GetProperty("rows")[0].GetProperty("detailVersion").GetString()!
                : CompactContentVersion.ForRow(updatedTool);
            delayed.Release.TrySetResult();
            using var response = await responseTask.WaitAsync(Budget);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await response.ReadJsonAsync();
            var responseVersion = body.RootElement.GetProperty("version").GetString();
            Assert.Equal(initialVersion, responseVersion);
            Assert.NotEqual(currentVersion, responseVersion);
            Assert.Equal(captured.Sequence, body.RootElement.GetProperty("sequence").GetInt64());
            Assert.True(body.RootElement.GetProperty("sequence").GetInt64() < newerSnapshot.RootElement.GetProperty("snapshot").GetProperty("sequence").GetInt64());
            Assert.True(delayed.IsDisposed);
            reconnect.Abort();
            liveSocket.Abort();
        }
        finally
        {
            delayed.Release.TrySetResult();
            harness.Runtime.UseProjection(session, null);
            socket.Abort();
        }
    }

    [Fact]
    public async Task CompactKernelProjection_StreamsThenFoldsAndExpandsWithoutChangingAuthoritativeRows()
    {
        var (session, token) = await harness.NewSessionAsync();
        var store = new SessionEventStore(session, TimeProvider.System);
        var turn = new TurnId("kernel-turn");
        var toolId = new ToolCallId("kernel-tool");
        var input = JsonSerializer.SerializeToElement(new { path = "changed.cs", content = InputSecret });
        store.Append(new TurnStartedEvent(turn, StrongId.NewCommandId()));
        store.Append(new UserMessageCommittedEvent(new ConversationMessage(new MessageId("kernel-user"), MessageRole.User,
            [new TextPart(new PartId("user-text"), "kernel request")], At)));
        store.Append(new ToolCallStartedEvent(turn, toolId, "Edit", input));
        store.Append(new ToolCallResultEvent(turn, toolId, "Edit",
            ToolExecutionResult.Text(OutputSecret, fileChange: new FileChangeStats(2, 1), fileChangePath: "changed.cs"), TimeSpan.FromSeconds(1)));
        store.Append(new ModelStreamingTextEvent(turn, "baseline"));
        harness.Runtime.UseProjection(session, () => new LiveConversationProjection(store, 0, () => 0));
        using var socket = await Connect(session, token, "compact=1&appendText=1");
        using var initial = JsonDocument.Parse(await ReceiveText(socket));
        var rows = initial.RootElement.GetProperty("snapshot").GetProperty("rows").EnumerateArray().Select(row => row.Clone()).ToList();
        Assert.Equal("user_message", Type(rows[0]));
        AssertToolHeader(rows[1]);
        Assert.DoesNotContain(OutputSecret, initial.RootElement.GetRawText());
        var chunk = store.Append(new ModelStreamingTextEvent(turn, " chunk"));
        using var chunkFrame = JsonDocument.Parse(await ReceiveText(socket));
        Assert.Equal(chunk.Sequence, chunkFrame.RootElement.GetProperty("delta").GetProperty("sequence").GetInt64());
        Assert.Contains(chunkFrame.RootElement.GetProperty("delta").GetProperty("ops").EnumerateArray(), op => Type(op) == "row_text_appended");
        var final = store.Append(new AssistantMessageCommittedEvent(new ConversationMessage(new MessageId("kernel-answer"), MessageRole.Assistant,
            [new TextPart(new PartId("answer-text"), "authoritative answer"), new ReasoningPart(new PartId("answer-reason"), ReasoningSecret)], At.AddSeconds(3))));
        using var finalFrame = JsonDocument.Parse(await ReceiveText(socket));
        Assert.Equal(final.Sequence, finalFrame.RootElement.GetProperty("delta").GetProperty("sequence").GetInt64());
        Apply(rows, finalFrame.RootElement.GetProperty("delta").GetProperty("ops"));
        Assert.DoesNotContain(rows, row => Type(row) == "turn_summary");
        var seal = store.Append(new TurnCompletedEvent(turn, TurnOutcome.Completed));
        var sealedText = await ReceiveText(socket);
        AssertNoHiddenPayload(sealedText);
        using var sealedFrame = JsonDocument.Parse(sealedText);
        Assert.Equal(seal.Sequence, sealedFrame.RootElement.GetProperty("delta").GetProperty("sequence").GetInt64());
        Apply(rows, sealedFrame.RootElement.GetProperty("delta").GetProperty("ops"));
        Assert.Equal(2, rows.Count);
        Assert.Equal("turn_summary", Type(rows[0]));
        Assert.Equal("file_change_summary", Type(rows[1]));
        var version = rows[0].GetProperty("detailVersion").GetString();
        using var detailsResponse = await Get(session, "turns/kernel-user", token, version);
        Assert.Equal(HttpStatusCode.OK, detailsResponse.StatusCode);
        using var details = await detailsResponse.ReadJsonAsync();
        Assert.Equal(new[] { "kernel-tool", "kernel-answer" }, Ids(details.RootElement.GetProperty("rows")));
        using var full = await Get(session, "rows/kernel-tool", token,
            details.RootElement.GetProperty("rows")[0].GetProperty("detailVersion").GetString());
        Assert.Contains(OutputSecret, await full.Content.ReadAsStringAsync());
        using (var authoritative = new LiveConversationProjection(store, 0, () => 0))
        {
            var snapshot = authoritative.Snapshot();
            Assert.Equal(4, snapshot.Rows.Count);
            Assert.Contains(snapshot.Rows.OfType<AssistantMessageRow>().Single().Parts, part => part is ReasoningPart reasoning && reasoning.Text == ReasoningSecret);
            Assert.Contains(snapshot.Rows.OfType<ToolCallRow>().Single().Output!, block => block is ToolResultText text && text.Text == OutputSecret);
        }
        store.Append(new ConversationRewoundEvent(new MessageId("kernel-user"), default, [], 0));
        using var rewound = JsonDocument.Parse(await ReceiveText(socket));
        Apply(rows, rewound.RootElement.GetProperty("delta").GetProperty("ops"));
        Assert.Empty(rows);
        using var missing = await Get(session, "turns/kernel-user", token, version);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        socket.Abort();
    }

    [Fact]
    public void ContentVersions_AreStableAcrossRehydration_AndIncludeHiddenPayloadChanges()
    {
        var tool = Tool("tool", JsonSerializer.SerializeToElement(new { content = new string('x', 5000) + "tail" }), new string('y', 5000) + "output-tail");
        var version = CompactContentVersion.ForRow(tool);
        var rehydrated = JsonSerializer.Deserialize<ConversationRow>(GatewayHarness.Serialize<ConversationRow>(tool), GatewayJson.Options)!;
        Assert.Equal(version, CompactContentVersion.ForRow(rehydrated));
        Assert.NotEqual(version, CompactContentVersion.ForRow(tool with { Input = JsonSerializer.SerializeToElement(new { content = new string('x', 5000) + "changed-tail" }) }));
        Assert.NotEqual(version, CompactContentVersion.ForRow(tool with { Output = [new ToolResultText(new string('y', 5000) + "changed-output-tail")] }));
        var answer = Assistant("answer", new ReasoningPart(new PartId("reason"), new string('z', 5000), ProviderSignature: "first"));
        Assert.NotEqual(CompactContentVersion.ForRow(answer), CompactContentVersion.ForRow(answer with { Parts = [new ReasoningPart(new PartId("reason"), new string('z', 5000), ProviderSignature: "second")] }));
    }

    [Fact]
    public void StreamingAppends_DoNotCopyBaselineOrScanHistoricalPayloads()
    {
        var few = Transcript(1);
        var many = Transcript(128);
        var fewBytes = Measure(few);
        var manyBytes = Measure(many);
        output.WriteLine($"64 append frames allocated: one historical turn={fewBytes:N0}; 128 historical turns={manyBytes:N0} bytes");
        Assert.True(manyBytes < fewBytes + 65_536, $"Appends allocated {manyBytes} versus {fewBytes} bytes.");
        Assert.True(manyBytes < 512_000, $"Appends allocated {manyBytes} bytes.");

        static CompactTranscript Transcript(int history)
        {
            List<ConversationRow> rows = [];
            for (var index = 0; index < history; index++)
                rows.AddRange([User($"user-{index}"), Tool($"tool-{index}"), Assistant($"answer-{index}", new TextPart(new PartId($"text-{index}"), "answer"))]);
            rows.Add(User("active-user"));
            rows.Add(Assistant("stream", new TextPart(new PartId("stream-text"), new string('s', 1_000_000))) with { IsStreaming = true });
            return new CompactTranscript(new ConversationSnapshot(0, 0, rows, State() with { Phase = TurnPhase.Streaming }));
        }

        static long Measure(CompactTranscript transcript)
        {
            _ = transcript.Apply(new ConversationDelta(1, [new RowTextAppended("stream", "warmup", At)]), true);
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 64; index++)
                _ = transcript.Apply(new ConversationDelta(index + 2, [new RowTextAppended("stream", "chunk", At)]), true);
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
    }

    private async Task WaitForObservers(SessionId session, int count)
    {
        using var timeout = new CancellationTokenSource(Budget);
        while (harness.Runtime.ObserverCount(session) != count)
            await Task.Delay(10, timeout.Token);
    }

    private sealed class ClosingProjection(IConversationProjection inner) : IConversationProjection
    {
        private bool _capturing;
        public bool BlockNextSnapshot { get; set; }
        public bool DisposedDuringCapture { get; private set; }
        public TaskCompletionSource CaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConversationSnapshot Snapshot()
        {
            var snapshot = inner.Snapshot();
            if (BlockNextSnapshot)
            {
                BlockNextSnapshot = false;
                _capturing = true;
                try
                {
                    CaptureStarted.TrySetResult();
                    Release.Task.WaitAsync(Budget).GetAwaiter().GetResult();
                }
                finally
                {
                    _capturing = false;
                }
            }
            return snapshot;
        }

        public IAsyncEnumerable<ConversationDelta> Deltas(CancellationToken cancellationToken) => inner.Deltas(cancellationToken);
        public Task WaitUntilSequenceAsync(long sequence, CancellationToken cancellationToken = default) => inner.WaitUntilSequenceAsync(sequence, cancellationToken);
        public void Dispose()
        {
            DisposedDuringCapture |= _capturing;
            inner.Dispose();
            Disposed.TrySetResult();
        }
    }

    private sealed class CapturedProjection(ConversationSnapshot snapshot) : IConversationProjection
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDisposed { get; private set; }

        public ConversationSnapshot Snapshot()
        {
            Requested.TrySetResult();
            Release.Task.WaitAsync(Budget).GetAwaiter().GetResult();
            return snapshot;
        }

        public IAsyncEnumerable<ConversationDelta> Deltas(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WaitUntilSequenceAsync(long sequence, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() => IsDisposed = true;
    }

    private Task<ClientWebSocket> Connect(SessionId session, string token, string query = "compact=1") =>
        WebSocketFrames.ConnectAsync(new Uri($"{harness.BaseAddress}api/sessions/{session.Value}/stream?token={Uri.EscapeDataString(token)}&{query}"), CancellationToken.None);

    private Task<HttpResponseMessage> Get(SessionId session, string path, string token, string? version = null) =>
        harness.GetWithTokenAsync($"api/sessions/{session.Value}/{path}" + (version is null ? "" : $"?version={Uri.EscapeDataString(version)}"), token);

    private static async Task<string> ReceiveText(ClientWebSocket socket)
    {
        var frame = await WebSocketFrames.ReceiveAsync(socket, Budget);
        Assert.False(frame.IsClose);
        return Assert.IsType<string>(frame.Text);
    }

    private static ConversationState State() => ConversationState.Initial("compact protocol", PermissionMode.Build, null, 200_000);
    private static UserMessageRow User(string id) => new(id, "User request", At, CanRewind: true);
    private static AssistantMessageRow Assistant(string id, params MessagePart[] parts) => new(id, new MessageId(id), parts, false, At.AddSeconds(3));
    private static ToolCallRow Tool(string id, JsonElement? input = null, string? output = null) => new(
        id, new ToolCallId(id), "Read", input ?? JsonSerializer.SerializeToElement(new { path = "file.cs" }),
        ToolCallStatus.Succeeded, [new ToolResultText(output ?? "result")], false, TimeSpan.FromSeconds(2), At,
        new FileChangeStats(1, 0));
    private static string? Type(JsonElement value) => value.GetProperty("$type").GetString();
    private static IEnumerable<string?> Ids(JsonElement rows) => rows.EnumerateArray().Select(row => row.GetProperty("rowId").GetString());

    private static void AssertNoHiddenPayload(string json)
    {
        Assert.DoesNotContain(InputSecret, json);
        Assert.DoesNotContain(OutputSecret, json);
        Assert.DoesNotContain(ReasoningSecret, json);
        Assert.DoesNotContain("private-signature", json);
    }

    private static JsonDocument ToolSnapshot(ToolCallRow tool) => JsonDocument.Parse(CompactJson.SnapshotFrame(0, 0,
        [new CompactWireRow(new CompactRowEntry(tool))], State()));

    private static void AssertPreview(JsonElement preview, ToolResultMedia image)
    {
        Assert.Equal(new[] { "artifactUri", "mimeType", "fileName" }, preview.EnumerateObject().Select(property => property.Name));
        Assert.Equal(image.ArtifactUri, preview.GetProperty("artifactUri").GetString());
        Assert.Equal(image.MimeType, preview.GetProperty("mimeType").GetString());
        Assert.Equal(image.FileName, preview.GetProperty("fileName").GetString());
    }

    private static void AssertToolHeader(JsonElement tool)
    {
        Assert.Equal("tool_call", Type(tool));
        Assert.True(tool.GetProperty("detailsDeferred").GetBoolean());
        Assert.False(string.IsNullOrEmpty(tool.GetProperty("detailVersion").GetString()));
        Assert.False(tool.TryGetProperty("output", out _));
        Assert.False(tool.TryGetProperty("text", out _));
        Assert.True(tool.TryGetProperty("fileChange", out _));
    }

    private static void Apply(List<JsonElement> rows, JsonElement operations)
    {
        foreach (var operation in operations.EnumerateArray())
        {
            switch (Type(operation))
            {
                case "row_appended":
                case "row_upserted":
                    var row = operation.GetProperty("row");
                    var index = rows.FindIndex(current => current.GetProperty("rowId").GetString() == row.GetProperty("rowId").GetString());
                    if (index < 0) rows.Add(row.Clone());
                    else rows[index] = row.Clone();
                    break;
                case "row_removed":
                    rows.RemoveAll(row => row.GetProperty("rowId").GetString() == operation.GetProperty("rowId").GetString());
                    break;
                case "row_text_appended":
                    break;
            }
        }
    }
}
