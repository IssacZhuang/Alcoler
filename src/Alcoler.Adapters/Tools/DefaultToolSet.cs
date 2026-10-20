using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public static class DefaultToolSet
{
    public static IReadOnlyList<ITool> Create() =>
    [
        new ReadFileTool(),
        new WriteFileTool(),
        new EditFileTool(),
        new GlobTool(),
        new GrepTool(),
        new BashTool(),
    ];
}
