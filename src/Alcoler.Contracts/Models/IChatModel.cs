namespace Alcoler.Contracts.Models;

public interface IChatModel
{
    ModelDescriptor Descriptor { get; }

    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}

public interface IModelProvider
{
    string Kind { get; }

    ValueTask<IChatModel> BindAsync(ModelSelection selection, CancellationToken cancellationToken);
}
