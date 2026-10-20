namespace Alcoler.Adapters.Tools;

internal static class MissingFileSuggestions
{
    private const int MaxSuggestionDistance = 3;

    public static string CreateMissingFileMessage(string filePath, string currentDirectory)
    {
        var suggestion = FindSimilarFilename(filePath);
        return $"File does not exist. Note: your current working directory is {currentDirectory}."
            + (suggestion is null ? "" : $" Did you mean {suggestion}?");
    }

    private static string? FindSimilarFilename(string filePath)
    {
        try
        {
            var parent = Path.GetDirectoryName(filePath);
            if (parent is null)
                return null;
            var targetName = Path.GetFileName(filePath);
            var targetStem = Path.GetFileNameWithoutExtension(filePath);
            var entries = Directory.EnumerateFiles(parent)
                .Select(static file => Path.GetFileName(file))!
                .Where(name => !string.Equals(name, targetName, StringComparison.Ordinal))
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();

            var sameStem = entries.FirstOrDefault(name =>
                string.Equals(Path.GetFileNameWithoutExtension(name), targetStem, StringComparison.Ordinal));
            if (sameStem is not null)
                return sameStem;

            return entries.FirstOrDefault(name =>
                EditMatchers.LevenshteinDistance(name, targetName) <= MaxSuggestionDistance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
