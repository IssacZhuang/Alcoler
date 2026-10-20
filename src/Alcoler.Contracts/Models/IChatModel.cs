namespace Alcoler.Contracts.Models;

public interface IChatModel
{
    ModelDescriptor Descriptor { get; }

    void ValidateOptions(ModelCallOptions options) => Descriptor.Properties.ValidateOptions(options);

    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}

public interface IModelProvider
{
    string Kind { get; }

    string Id => Kind;

    ValueTask<IChatModel> BindAsync(ModelSelection selection, CancellationToken cancellationToken);
}
