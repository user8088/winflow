using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace WinFlow.App;

/// <summary>
/// Draws simple colored-dot tray icons at runtime until real art exists.
/// </summary>
public static class TrayIconFactory
{
    public static byte[] CreateIcoBytes(Color color)
    {
        const int size = 32;

        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var fill = new SolidBrush(color))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            graphics.FillEllipse(fill, 4, 4, size - 8, size - 8);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        byte[] pngBytes = png.ToArray();

        byte[] ico = new byte[6 + 16 + pngBytes.Length];
        Span<byte> header = ico.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 1);
        header[6] = size;
        header[7] = size;
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], pngBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], 6 + 16);

        pngBytes.CopyTo(ico.AsSpan(22));
        return ico;
    }

    public static Icon CreateIcon(byte[] icoBytes)
    {
        using var stream = new MemoryStream(icoBytes);
        return new Icon(stream);
    }
}
