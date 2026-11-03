using System.Buffers.Binary;
using System.Text;
using Alcoler.Adapters.Storage;
using SkiaSharp;
using Xunit;

namespace Alcoler.Kernel.Tests.Storage;

public sealed class ArtifactImageThumbnailTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public async Task RasterImages_AreReencodedAsValidDownscaledPng(SKEncodedImageFormat format)
    {
        var source = CreateRaster(format, 1024, 512);
        var storage = new byte[source.Length + 12];
        source.CopyTo(storage, 7);
        var result = await ArtifactImageThumbnail.GenerateAsync(storage.AsMemory(7, source.Length), CancellationToken.None);
        Assert.True(result.HasValue);
        Assert.Equal("image/png", ArtifactImageThumbnail.MimeType);
        using var data = SKData.CreateCopy(result.Value.Span);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
        Assert.Equal(256, codec.Info.Width);
        Assert.Equal(128, codec.Info.Height);
        using var decoded = SKBitmap.Decode(result.Value.ToArray());
        Assert.NotNull(decoded);
        Assert.InRange(result.Value.Length, 1, 256 * 256 * 4 + 4096);
        Assert.Equal(source, storage.AsSpan(7, source.Length).ToArray());
    }

    [Fact]
    public async Task SmallImage_IsNotUpscaled_AndPreservesTransparency()
    {
        using var source = new SKBitmap(16, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        source.Erase(new SKColor(30, 60, 90, 120));
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var result = await ArtifactImageThumbnail.GenerateAsync(encoded.ToArray(), CancellationToken.None);
        Assert.True(result.HasValue);
        using var decoded = SKBitmap.Decode(result.Value.ToArray());
        Assert.Equal(16, decoded.Width);
        Assert.Equal(10, decoded.Height);
        Assert.InRange(decoded.GetPixel(0, 0).Alpha, (byte)1, (byte)254);
    }

    [Fact]
    public async Task Gif_IsDecodedIntoStaticPng()
    {
        var source = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        var result = await ArtifactImageThumbnail.GenerateAsync(source, CancellationToken.None);
        Assert.True(result.HasValue);
        using var data = SKData.CreateCopy(result.Value.Span);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
        Assert.Equal(1, codec.Info.Width);
        Assert.Equal(1, codec.Info.Height);
    }

    [Theory]
    [InlineData(4097, 4097)]
    [InlineData(16_385, 1)]
    [InlineData(1, 16_385)]
    public async Task OversizedDecodedDimensions_AreRejectedBeforePixelAllocation(int width, int height)
    {
        var source = CreateRaster(SKEncodedImageFormat.Png, 1, 1);
        BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(20, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(29, 4), Crc32(source.AsSpan(12, 17)));
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        Assert.Equal(width, codec.Info.Width);
        Assert.Equal(height, codec.Info.Height);
        Assert.Null(await ArtifactImageThumbnail.GenerateAsync(source, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedEncodedInput_IsRejected()
    {
        var source = new byte[ArtifactImageThumbnail.MaxInputBytes + 1];
        Assert.Null(await ArtifactImageThumbnail.GenerateAsync(source, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an image")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>")]
    [InlineData("<html><body>not a raster image</body></html>")]
    public async Task InvalidOrUnsupportedInput_IsOmittedWithoutReturningOriginal(string content)
    {
        Assert.Null(await ArtifactImageThumbnail.GenerateAsync(Encoding.UTF8.GetBytes(content), CancellationToken.None));
    }

    [Fact]
    public async Task ValidButUnsupportedBmp_IsOmitted()
    {
        var source = new byte[58];
        source[0] = (byte)'B';
        source[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(2), source.Length);
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(22), 1);
        BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(34), 4);
        source[56] = 255;
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Bmp, codec.EncodedFormat);
        Assert.Null(await ArtifactImageThumbnail.GenerateAsync(source, CancellationToken.None));
    }

    [Fact]
    public async Task CorruptRaster_IsOmitted()
    {
        var source = CreateRaster(SKEncodedImageFormat.Png, 16, 16);
        Assert.Null(await ArtifactImageThumbnail.GenerateAsync(source.AsMemory(0, 40), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_IsObservedBeforeDecoding()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ArtifactImageThumbnail.GenerateAsync(CreateRaster(SKEncodedImageFormat.Png, 16, 16), cancellation.Token));
    }

    private static byte[] CreateRaster(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        return encoded?.ToArray() ?? throw new InvalidOperationException("Unable to encode the test image.");
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }
}
