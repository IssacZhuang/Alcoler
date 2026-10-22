using System.Runtime.CompilerServices;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Models;

public sealed class SessionAuxModelClientTests
{
    [Fact]
    public async Task SearchAsync_SendsProviderNativeWebSearchOnSessionModel()
    {
        var model = new RecordingChatModel(new ModelProperties(
            DefaultMaxOutputTokens: 32_768,
            SupportsNativeWebSearch: true,
            ReasoningLevels: ["low", "high"]));
        var client = new SessionAuxModelClient(
            () => new ModelSelection("test", "model",
                new ModelCallOptions(ReasoningLevel: "high", MaxOutputTokens: 1_234)),
            _ => model);

        var response = await client.SearchAsync(
            new ProviderWebSearchRequest("dotnet", ["example.com"], ["blocked.example"]),
            CancellationToken.None);

        Assert.Equal("Summary text", response.Summary);
        Assert.Equal(["https://a.example", "https://b.example"],
            response.Sources.Select(source => source.Url));
        Assert.Equal(2, response.WebSearchRequests);

        var request = Assert.Single(model.Requests);
        var tool = Assert.Single(request.Tools);
        Assert.Equal(ProviderNativeWebSearch.ToolName, tool.Name);
        var native = Assert.IsType<ProviderNativeToolSpec>(tool.ProviderNative);
        Assert.Equal(ProviderNativeWebSearch.ToolType, native.Type);
        Assert.Equal(ProviderNativeWebSearch.ToolName, native.Name);
        Assert.Equal(8, native.Args["max_uses"]);
        Assert.Equal(new object?[] { "example.com" },
            Assert.IsType<object?[]>(native.Args["allowed_domains"]));
        Assert.Equal(new object?[] { "blocked.example" },
            Assert.IsType<object?[]>(native.Args["blocked_domains"]));
        Assert.Equal("low", request.Options.ReasoningLevel);
        Assert.Equal(4_096, request.Options.MaxOutputTokens);
        Assert.Contains("Perform a web search for the query: dotnet",
            request.Messages.Single(message => message.Role == MessageRole.User)
                .Content.OfType<InputTextBlock>().Single().Text);
    }

    [Fact]
    public void IsAvailable_ReflectsSessionModelCapability()
    {
        var native = new SessionAuxModelClient(
            () => new ModelSelection("test", "model"),
            _ => new RecordingChatModel(new ModelProperties(SupportsNativeWebSearch: true)));
        var plain = new SessionAuxModelClient(
            () => new ModelSelection("test", "model"),
            _ => new RecordingChatModel(new ModelProperties()));

        Assert.True(native.IsAvailable);
        Assert.False(plain.IsAvailable);
    }

    [Fact]
    public async Task SearchAsync_RejectsModelsWithoutNativeSearch()
    {
        var client = new SessionAuxModelClient(
            () => new ModelSelection("test", "model"),
            _ => new RecordingChatModel(new ModelProperties()));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SearchAsync(new ProviderWebSearchRequest("x", [], []), CancellationToken.None).AsTask());

        Assert.Contains("native WebSearch", error.Message);
    }

    private sealed class RecordingChatModel(ModelProperties properties) : IChatModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public ModelDescriptor Descriptor { get; } = new("test", "model", "Test Model", properties);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ModelStreamStarted();
            yield return new ModelTextStarted();
            yield return new ModelTextDelta("Summary text");
            yield return new ModelTextEnded();
            yield return new ModelSourceFound("https://a.example", "A");
            yield return new ModelSourceFound("https://b.example", "B");
            yield return new ModelStreamFinished(ModelFinishReason.Stop,
                new TokenUsage(ServerWebSearchRequests: 2));
        }
    }
}
