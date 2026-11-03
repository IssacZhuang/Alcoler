using System.Text.Json;
using Alcoler.Contracts.Conversation;

namespace Alcoler.Remote;

internal sealed class CompactImagePreviews
{
    public const int MaxCount = 8;
    public const int MaxUriLength = 2048;
    public const int MaxFileNameLength = 256;
    private static readonly string[] SafeMimeTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];
    private readonly List<ToolResultMedia> _items = [];
    private bool _truncated;

    public static CompactImagePreviews Extract(IReadOnlyList<ToolResultBlock>? output)
    {
        var previews = new CompactImagePreviews();
        if (output is null)
            return previews;
        foreach (var block in output)
        {
            if (block is not ToolResultMedia media)
                continue;
            var mime = media.MimeType.AsSpan();
            var parameterStart = mime.IndexOf(';');
            if (parameterStart >= 0) mime = mime[..parameterStart];
            mime = mime.Trim();
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                continue;
            var mimeType = SafeMimeType(mime);
            var artifactUri = media.ArtifactUri;
            if (mimeType is null || string.IsNullOrEmpty(artifactUri) || artifactUri.Length > MaxUriLength
                || !artifactUri.StartsWith("alcoler-artifact://", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(artifactUri, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            {
                previews._truncated = true;
                continue;
            }
            if (previews._items.Any(item => item.ArtifactUri == artifactUri))
                continue;
            if (previews._items.Count == MaxCount)
            {
                previews._truncated = true;
                return previews;
            }
            var fileName = media.FileName;
            if (fileName?.Length > MaxFileNameLength)
            {
                var end = MaxFileNameLength - 3;
                if (char.IsHighSurrogate(fileName[end - 1])) end--;
                fileName = fileName[..end] + "...";
            }
            previews._items.Add(media with { MimeType = mimeType, FileName = fileName });
        }
        return previews;
    }

    private static string? SafeMimeType(ReadOnlySpan<char> mimeType)
    {
        foreach (var candidate in SafeMimeTypes)
            if (mimeType.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
        return null;
    }

    public long BudgetBytes => (_items.Count > 0
        ? 20 + _items.Sum(item => 48 + CompactHistoryBudget.StringBytes(item.ArtifactUri)
            + CompactHistoryBudget.StringBytes(item.MimeType) + CompactHistoryBudget.StringBytes(item.FileName))
        : 0) + (_truncated ? 30 : 0);

    public void Write(Utf8JsonWriter writer)
    {
        if (_items.Count > 0)
        {
            writer.WriteStartArray("imagePreviews");
            foreach (var item in _items)
            {
                writer.WriteStartObject();
                writer.WriteString("artifactUri", item.ArtifactUri);
                writer.WriteString("mimeType", item.MimeType);
                writer.WriteString("fileName", item.FileName);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (_truncated)
            writer.WriteBoolean("imagePreviewsTruncated", true);
    }
}
