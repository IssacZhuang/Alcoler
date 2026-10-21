namespace Alcoler.Contracts.Models;

public interface IChatModel
{
    ModelDescriptor Descriptor { get; }

    bool InlineToolResultImages => false;

    IChatModel CreateRequestScope() => this;

    bool SupportsNonStreamingGeneration => false;

    ValueTask<IReadOnlyList<ModelStreamEvent>> GenerateAsync(ModelRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This model does not support non-streaming generation.");

    void ValidateOptions(ModelCallOptions options) => Descriptor.Properties.ValidateOptions(options);

    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}

public interface IModelProvider
{
    string Kind { get; }

    string Id => Kind;

    ValueTask<IChatModel> BindAsync(ModelSelection selection, CancellationToken cancellationToken);
}
