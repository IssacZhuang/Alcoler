namespace Alcoler.Kernel.Models;

internal static class ModelImageFormats
{
    public static string? Extension(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => null,
    };

    public static string? CanonicalMimeType(string? mimeType) =>
        Extension(mimeType) is not null ? mimeType!.ToLowerInvariant() : null;
}
