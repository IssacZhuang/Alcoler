using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Alcoler.Workbench.ViewModel;

namespace Alcoler.Workbench.Ui;

internal static class WindowsClipboardImages
{
    private const uint DibFormat = 8;
    private const uint DibV5Format = 17;
    private const int BitmapFileHeaderSize = 14;

    internal static async Task<byte[]?> ReadAsync()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        if (!IsClipboardFormatAvailable(DibFormat) && !IsClipboardFormatAvailable(DibV5Format))
            return null;

        for (var attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt == 4)
                throw ClipboardError("The clipboard is currently in use.");
            await Task.Delay(20);
        }
        try
        {
            var format = IsClipboardFormatAvailable(DibV5Format) ? DibV5Format : DibFormat;
            if (!IsClipboardFormatAvailable(format))
                return null;
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero)
                throw ClipboardError("The clipboard bitmap could not be read.");
            var size = GlobalSize(handle).ToUInt64();
            if (size > ComposerImageAttachment.MaxImageBytes - BitmapFileHeaderSize)
                throw new InvalidDataException("Images must be 20 MiB or smaller.");
            ComposerImageAttachment.ValidateSize((long)size);
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                throw ClipboardError("The clipboard bitmap could not be locked.");
            try
            {
                var dib = new byte[(int)size];
                Marshal.Copy(pointer, dib, 0, dib.Length);
                return dib;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    internal static byte[] CreateBitmapFile(ReadOnlySpan<byte> dib)
    {
        ComposerImageAttachment.ValidateSize((long)dib.Length + BitmapFileHeaderSize);
        if (dib.Length < 40)
            throw InvalidBitmap();
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (headerSize is not (40 or 52 or 56 or 108 or 124) || headerSize > dib.Length)
            throw InvalidBitmap();
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(dib[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        var colors = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (width <= 0 || height == 0 || planes != 1 || bits is not (1 or 4 or 8 or 16 or 24 or 32)
            || compression is not (0 or 3 or 6) || (compression != 0 && bits is not (16 or 32)))
            throw InvalidBitmap();
        if (bits <= 8)
        {
            if (colors > 1u << bits)
                throw InvalidBitmap();
            if (colors == 0)
                colors = 1u << bits;
        }
        var masks = headerSize == 40 ? compression switch { 3 => 12, 6 => 16, _ => 0 } : 0;
        var pixelOffset = headerSize + (long)colors * 4 + masks;
        var stride = ((long)width * bits + 31) / 32 * 4;
        if (pixelOffset > dib.Length || Math.Abs((long)height) > (dib.Length - pixelOffset) / stride)
            throw InvalidBitmap();

        var bitmap = new byte[dib.Length + BitmapFileHeaderSize];
        bitmap[0] = (byte)'B';
        bitmap[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(2), (uint)bitmap.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(10), (uint)(pixelOffset + BitmapFileHeaderSize));
        dib.CopyTo(bitmap.AsSpan(BitmapFileHeaderSize));
        return bitmap;
    }

    private static InvalidDataException InvalidBitmap() => new("The clipboard contains an invalid or unsupported DIB bitmap.");

    private static IOException ClipboardError(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);
}
