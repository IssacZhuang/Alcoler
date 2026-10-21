using System.Buffers;
using System.Runtime.InteropServices;
using Alcoler.Workbench.ViewModel;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Alcoler.Workbench.Ui;

public static class ComposerClipboardImages
{
    private static readonly (string Format, string Mime, string Name)[] ImageFormats =
    [
        ("image/png", "image/png", "Clipboard image.png"),
        ("PNG", "image/png", "Clipboard image.png"),
        ("image/jpeg", "image/jpeg", "Clipboard image.jpg"),
    ];

    public static Task<IReadOnlyList<ComposerImageAttachment>> ReadAsync(IClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        return ReadAsync(clipboard, clipboard.GetType().Assembly.GetName().Name == "Avalonia.Win32"
            ? WindowsClipboardImages.ReadAsync
            : () => Task.FromResult<byte[]?>(null));
    }

    internal static async Task<IReadOnlyList<ComposerImageAttachment>> ReadAsync(
        IClipboard clipboard, Func<Task<byte[]?>> readNativeDib)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        try
        {
            var formats = await clipboard.GetFormatsAsync();
            foreach (var imageFormat in ImageFormats)
            {
                var format = formats.FirstOrDefault(format => string.Equals(format, imageFormat.Format, StringComparison.OrdinalIgnoreCase));
                if (format is null)
                    continue;
                var data = await clipboard.GetDataAsync(format);
                if (data is null)
                    continue;
                var bytes = await ReadBytesAsync(data);
                return [ComposerImageAttachment.FromBytes(bytes, imageFormat.Mime, imageFormat.Name)];
            }

            foreach (var dibFormat in new[] { "DeviceIndependentBitmapV5", "DeviceIndependentBitmap", "CF_DIBV5", "CF_DIB", "Unknown_Format_17", "Unknown_Format_8" })
            {
                var format = formats.FirstOrDefault(format => string.Equals(format, dibFormat, StringComparison.OrdinalIgnoreCase));
                if (format is not null && await clipboard.GetDataAsync(format) is { } data)
                    return [FromDib(await ReadBytesAsync(data))];
            }

            if (formats.Contains(DataFormats.Files) && await clipboard.GetDataAsync(DataFormats.Files) is IEnumerable<IStorageItem> files)
            {
                var attachments = new List<ComposerImageAttachment>();
                try
                {
                    foreach (var file in files.OfType<IStorageFile>())
                    {
                        var mime = GetFileMimeType(file.Name);
                        if (mime is null)
                            continue;
                        var properties = await file.GetBasicPropertiesAsync();
                        if (properties.Size is { } size)
                        {
                            if (size > ComposerImageAttachment.MaxImageBytes)
                                throw new InvalidDataException("Images must be 20 MiB or smaller.");
                            ComposerImageAttachment.ValidateSize((long)size);
                        }
                        await using var stream = await file.OpenReadAsync();
                        var bytes = await ReadStreamAsync(stream);
                        attachments.Add(mime == "image/bmp"
                            ? ComposerImageAttachment.FromBitmapBytes(bytes, Path.ChangeExtension(file.Name, ".png"))
                            : ComposerImageAttachment.FromBytes(bytes, mime, file.Name));
                    }
                    if (attachments.Count > 0)
                        return attachments;
                }
                catch
                {
                    foreach (var attachment in attachments)
                        attachment.Dispose();
                    throw;
                }
            }

            var dib = await readNativeDib();
            return dib is null ? [] : [FromDib(dib)];
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or COMException or TimeoutException or NotSupportedException)
        {
            throw new IOException("The clipboard image could not be read.", exception);
        }
    }

    internal static ComposerImageAttachment FromDib(byte[] dib) =>
        ComposerImageAttachment.FromBitmapBytes(WindowsClipboardImages.CreateBitmapFile(dib), "Clipboard image.png");

    private static string? GetFileMimeType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => null,
    };

    private static async Task<byte[]> ReadBytesAsync(object data)
    {
        switch (data)
        {
            case byte[] bytes:
                ComposerImageAttachment.ValidateSize(bytes.LongLength);
                return bytes;
            case ReadOnlyMemory<byte> memory:
                ComposerImageAttachment.ValidateSize(memory.Length);
                return memory.ToArray();
            case Memory<byte> memory:
                ComposerImageAttachment.ValidateSize(memory.Length);
                return memory.ToArray();
            case Stream stream:
                return await ReadStreamAsync(stream);
            default:
                throw new InvalidDataException("The clipboard image has an unsupported data representation.");
        }
    }

    private static async Task<byte[]> ReadStreamAsync(Stream stream)
    {
        if (stream.CanSeek)
            ComposerImageAttachment.ValidateSize(stream.Length - stream.Position);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, ComposerImageAttachment.MaxImageBytes - (int)output.Length + 1)));
                if (read == 0)
                    break;
                var size = checked((int)output.Length + read);
                ComposerImageAttachment.ValidateSize(size);
                if (size > output.Capacity)
                    output.Capacity = (int)Math.Min(ComposerImageAttachment.MaxImageBytes, Math.Max(size, (long)output.Capacity * 2));
                output.Write(buffer, 0, read);
            }
            ComposerImageAttachment.ValidateSize(output.Length);
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
