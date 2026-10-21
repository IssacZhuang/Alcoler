using System.Buffers.Binary;
using Alcoler.Contracts.Models;
using Avalonia;
using Avalonia.Media.Imaging;

namespace Alcoler.Workbench.ViewModel;

public sealed class ComposerImageAttachment : IDisposable
{
    public const int MaxImageBytes = 20 * 1024 * 1024;
    private const long MaxDecodedPixels = 16_777_216;
    private const int MaxImageDimension = 32_768;
    private readonly FileStream _payload;
    private readonly string _mimeType;
    private bool _disposed;

    private ComposerImageAttachment(FileStream payload, string mimeType, Bitmap thumbnail, string name)
    {
        _payload = payload;
        _mimeType = mimeType;
        Thumbnail = thumbnail;
        Name = name;
    }

    public InputImageBlock Image
    {
        get
        {
            lock (_payload)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ValidateSize(_payload.Length);
                var bytes = new byte[(int)_payload.Length];
                _payload.Position = 0;
                _payload.ReadExactly(bytes);
                return new InputImageBlock(_mimeType, Convert.ToBase64String(bytes));
            }
        }
    }
    public Bitmap Thumbnail { get; }
    public string Name { get; }
    internal string PayloadPath => _payload.Name;
    internal string MimeType => _mimeType;

    internal byte[] ReadPreviewBytes()
    {
        lock (_payload)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateSize(_payload.Length);
            var bytes = new byte[(int)_payload.Length];
            _payload.Position = 0;
            _payload.ReadExactly(bytes);
            return bytes;
        }
    }

    public static ComposerImageAttachment FromBytes(byte[] bytes, string mimeType, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(mimeType);
        var mime = mimeType.ToLowerInvariant();
        var thumbnail = DecodeThumbnail(bytes, mime);
        FileStream? payload = null;
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "Alcoler", "composer-images");
            Directory.CreateDirectory(directory);
            payload = new FileStream(Path.Combine(directory, $"{Guid.NewGuid():N}.image"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose);
            payload.Write(bytes);
            payload.Flush();
            return new ComposerImageAttachment(payload, mime, thumbnail, name);
        }
        catch
        {
            payload?.Dispose();
            thumbnail.Dispose();
            throw;
        }
    }

    internal static Bitmap DecodeThumbnail(byte[] bytes, string mimeType, int maxDimension = 160)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDimension);
        var size = ReadImageSize(bytes, mimeType);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var longestSide = Math.Max(size.Width, size.Height);
            if ((long)Math.Min(size.Width, size.Height) * maxDimension < longestSide)
            {
                using var bitmap = new Bitmap(stream);
                return bitmap.CreateScaledBitmap(new PixelSize(
                    Math.Max(1, (int)((long)size.Width * maxDimension / longestSide)),
                    Math.Max(1, (int)((long)size.Height * maxDimension / longestSide))));
            }
            var thumbnail = size.Height > size.Width
                ? Bitmap.DecodeToHeight(stream, Math.Min(size.Height, maxDimension))
                : Bitmap.DecodeToWidth(stream, Math.Min(size.Width, maxDimension));
            if (thumbnail.PixelSize.Width > maxDimension || thumbnail.PixelSize.Height > maxDimension)
            {
                thumbnail.Dispose();
                throw InvalidImage();
            }
            return thumbnail;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            throw new InvalidDataException("The clipboard image could not be decoded.", exception);
        }
    }

    internal static ComposerImageAttachment FromBitmapBytes(byte[] bytes, string name)
    {
        ReadImageSize(bytes, "image/bmp");
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var bitmap = new Bitmap(input);
            using var output = new MemoryStream();
            bitmap.Save(output);
            ValidateSize(output.Length);
            return FromBytes(output.ToArray(), "image/png", name);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            throw new InvalidDataException("The clipboard bitmap could not be decoded.", exception);
        }
    }

    internal static void ValidateSize(long size)
    {
        if (size <= 0)
            throw new InvalidDataException("The clipboard image is empty.");
        if (size > MaxImageBytes)
            throw new InvalidDataException("Images must be 20 MiB or smaller.");
    }

    private static PixelSize ReadImageSize(ReadOnlySpan<byte> bytes, string mime)
    {
        ValidateSize(bytes.Length);
        var size = mime switch
        {
            "image/png" when bytes.Length >= 33 && bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })
                && BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes.Slice(12, 4).SequenceEqual("IHDR"u8) =>
                ValidatedSize(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..])),
            "image/gif" when bytes.Length >= 10 && (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) =>
                ValidatedSize(BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..])),
            "image/jpeg" when bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff => ReadJpegSize(bytes),
            "image/webp" when bytes.Length >= 20 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8) => ReadWebpSize(bytes),
            "image/bmp" when bytes.Length >= 54 && bytes[..2].SequenceEqual("BM"u8) =>
                ValidatedSize(BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]), Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]))),
            _ => throw InvalidImage(),
        };
        return size;
    }

    private static PixelSize ReadJpegSize(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        while (offset + 3 < bytes.Length && bytes[offset] == 0xff)
        {
            var marker = bytes[offset + 1];
            if (marker == 0xff)
            {
                offset++;
                continue;
            }
            if (marker is 0xda or 0xd9)
                break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 2)..]);
            if (length < 2 || length > bytes.Length - offset - 2)
                break;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc) && length >= 7)
                return ValidatedSize(BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]));
            offset += length + 2;
        }
        throw InvalidImage();
    }

    private static PixelSize ReadWebpSize(ReadOnlySpan<byte> bytes)
    {
        var offset = 12;
        while (offset <= bytes.Length - 8)
        {
            var chunk = bytes.Slice(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            if (length > bytes.Length - offset - 8)
                break;
            var data = bytes.Slice(offset + 8, (int)length);
            if (chunk.SequenceEqual("VP8X"u8) && data.Length >= 10)
                return ValidatedSize(1 + ReadUInt24(data[4..]), 1 + ReadUInt24(data[7..]));
            if (chunk.SequenceEqual("VP8L"u8) && data.Length >= 5 && data[0] == 0x2f)
            {
                var dimensions = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
                return ValidatedSize(1 + (dimensions & 0x3fff), 1 + ((dimensions >> 14) & 0x3fff));
            }
            if (chunk.SequenceEqual("VP8 "u8) && data.Length >= 10 && data.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                return ValidatedSize(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3fff);
            offset += 8 + (int)length + ((int)length & 1);
        }
        throw InvalidImage();
    }

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;

    private static PixelSize ValidatedSize(long width, long height)
    {
        if (width <= 0 || height <= 0)
            throw InvalidImage();
        if (width > MaxImageDimension || height > MaxImageDimension || width * height > MaxDecodedPixels)
            throw new InvalidDataException("Images must not exceed 16 megapixels or 32,768 pixels on either side.");
        return new PixelSize((int)width, (int)height);
    }

    private static InvalidDataException InvalidImage() => new("The clipboard image has an invalid or unsupported image header.");

    public void Dispose()
    {
        lock (_payload)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                Thumbnail.Dispose();
            }
            finally
            {
                _payload.Dispose();
            }
        }
    }
}
