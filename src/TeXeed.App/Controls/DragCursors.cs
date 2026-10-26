using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TeXeed.App.Controls;

/// <summary>Application-owned cursors, generated from vector paths at several DPI sizes.</summary>
public static class DragCursors
{
    private static readonly Lazy<Cursor> OpenHand = new(() => Create(false));
    private static readonly Lazy<Cursor> ClosedHand = new(() => Create(true));
    public static Cursor Grab => OpenHand.Value;
    public static Cursor Grabbing => ClosedHand.Value;

    private static Cursor Create(bool closed)
    {
        var outline = Geometry.Parse(closed
            ? "M10.5,28 L10.1,25.7 C8.3,24.6 6.8,22.8 6.1,20.7 L4.9,16.8 C4.4,15.2 5.2,13.7 6.6,13.3 C7.4,13.1 8.1,13.4 8.7,14 L8.7,10.6 C8.7,8.6 9.7,7.5 11.1,7.5 C12.3,7.5 13.1,8.3 13.3,9.6 L13.3,8.5 C13.3,6.6 14.3,5.6 15.7,5.6 C17.1,5.6 18.1,6.6 18.1,8.5 L18.1,9.3 C18.3,7.9 19.3,7 20.6,7 C22,7 22.9,8.1 22.9,10 L22.9,11 C23.1,9.9 24,9.2 25.1,9.4 C26.6,9.6 27.3,10.9 27.1,12.7 L26.5,19.2 C26.3,21.8 25.2,23.8 23.2,25.6 L23.1,28 Z"
            : "M11,28 C11,25 9,24 7,21 L3,16 C2,14 4,12 6,14 L10,18 L8,8 C8,5 11,4 12,7 L14,14 L13,4 C13,1 17,1 17,4 L18,14 L19,5 C19,2 23,3 22,6 L22,15 L24,9 C25,6 28,7 27,10 L26,21 C25,24 23,25 23,28 Z");
        // The four staggered knuckle peaks define the fist. Keep the palm blank.
        var images = new[] { 32, 48, 64 }.Select(size => (Size: size, Data: Render(size, outline))).ToArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0); writer.Write((ushort)2); writer.Write((ushort)images.Length);
            var offset = 6 + images.Length * 16;
            foreach (var (size, data) in images)
            {
                writer.Write((byte)size); writer.Write((byte)size); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)(16 * size / 32)); writer.Write((ushort)(18 * size / 32));
                writer.Write(data.Length); writer.Write(offset); offset += data.Length;
            }
            foreach (var (_, data) in images) writer.Write(data);
        }
        stream.Position = 0;
        return new Cursor(stream, scaleWithDpi: true);
    }

    private static byte[] Render(int size, Geometry outline)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(size / 32d, size / 32d));
            var edge = new Pen(Brushes.Black, 1.4) { LineJoin = PenLineJoin.Round };
            drawing.DrawGeometry(Brushes.White, edge, outline);
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[size * size * 4]; bitmap.CopyPixels(pixels, size * 4, 0);
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data);
        var maskStride = (size + 31) / 32 * 4;
        writer.Write(40); writer.Write(size); writer.Write(size * 2); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(pixels.Length + maskStride * size);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        // CUR uses bottom-up BGRA plus a monochrome transparency mask.
        for (var y = size - 1; y >= 0; y--)
            for (var x = 0; x < size; x++)
            {
                var index = (y * size + x) * 4; var alpha = pixels[index + 3];
                for (var c = 0; c < 3; c++) writer.Write(alpha == 0 ? (byte)0 : (byte)Math.Min(255, (pixels[index + c] * 255 + alpha / 2) / alpha));
                writer.Write(alpha);
            }
        for (var y = size - 1; y >= 0; y--)
        {
            var mask = new byte[maskStride];
            for (var x = 0; x < size; x++) if (pixels[(y * size + x) * 4 + 3] == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
            writer.Write(mask);
        }
        return data.ToArray();
    }
}
