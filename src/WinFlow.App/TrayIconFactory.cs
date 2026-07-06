using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace WinFlow.App;

/// <summary>
/// Draws simple colored-dot tray icons at runtime until real art exists.
///
/// Icons are produced as in-memory .ico files (PNG-compressed, supported
/// since Vista) rather than via Bitmap.GetHicon, so every <see cref="Icon"/>
/// constructed from them owns its data outright — no shared GDI handles
/// that another component's dispose can invalidate behind our back.
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
        // ICONDIR: reserved(0), type 1 = icon, count 1
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 1);
        // ICONDIRENTRY
        header[6] = size;                                                   // width
        header[7] = size;                                                   // height
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 1);          // color planes
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 32);         // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], pngBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], 6 + 16);      // image data offset

        pngBytes.CopyTo(ico.AsSpan(22));
        return ico;
    }

    public static Icon CreateIcon(byte[] icoBytes)
    {
        using var stream = new MemoryStream(icoBytes);
        return new Icon(stream);
    }
}
