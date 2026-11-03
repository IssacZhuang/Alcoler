using SkiaSharp;

namespace Alcoler.Adapters.Storage;

public static class ArtifactImageThumbnail
{
    public const string MimeType = "image/png";
    public const int MaxEdge = 256;
    public const int MaxInputBytes = 32 * 1024 * 1024;
    internal const long MaxDecodedPixels = 16_777_216;
    private const int MaxSourceEdge = 16_384;

    public static ValueTask<ReadOnlyMemory<byte>?> GenerateAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Generate(input, cancellationToken));
    }

    private static ReadOnlyMemory<byte>? Generate(ReadOnlyMemory<byte> input, CancellationToken token)
    {
        if (input.IsEmpty || input.Length > MaxInputBytes)
            return null;
        using var data = SKData.CreateCopy(input.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg
            or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Gif))
            return null;
        var info = codec.Info;
        if (!FitsDecodeBudget(info.Width, info.Height))
            return null;
        var scale = Math.Min(1.0, MaxEdge / (double)Math.Max(info.Width, info.Height));
        var desired = codec.GetScaledDimensions((float)scale);
        if (!FitsDecodeBudget(desired.Width, desired.Height))
            return null;
        token.ThrowIfCancellationRequested();
        using var decoded = new SKBitmap(new SKImageInfo(desired.Width, desired.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (decoded.GetPixels() == IntPtr.Zero
            || codec.GetPixels(decoded.Info, decoded.GetPixels(), new SKCodecOptions(0)) != SKCodecResult.Success)
            return null;
        token.ThrowIfCancellationRequested();
        var ratio = Math.Min(1.0, MaxEdge / (double)Math.Max(decoded.Width, decoded.Height));
        var width = Math.Max(1, (int)Math.Round(decoded.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(decoded.Height * ratio));
        using var frame = decoded.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
        if (frame is null)
            return null;
        token.ThrowIfCancellationRequested();
        using var image = SKImage.FromBitmap(frame);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        token.ThrowIfCancellationRequested();
        if (png is null || png.Size > MaxEdge * MaxEdge * 4 + 4096)
            return null;
        return png.ToArray();
    }

    private static bool FitsDecodeBudget(int width, int height) =>
        width is > 0 and <= MaxSourceEdge && height is > 0 and <= MaxSourceEdge
        && (long)width * height <= MaxDecodedPixels;
}
