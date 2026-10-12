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
            ? "M8,24 L6,18 C5,15 6,12 8,12 L10,12 L10,10 C10,8 14,8 14,10 C14,7 18,7 18,10 C18,8 22,8 22,11 C22,9 26,10 26,13 L26,20 C26,23 23,25 23,28 L11,28 C11,26 9,26 8,24 Z"
            : "M11,28 C11,25 9,24 7,21 L3,16 C2,14 4,12 6,14 L10,18 L8,8 C8,5 11,4 12,7 L14,14 L13,4 C13,1 17,1 17,4 L18,14 L19,5 C19,2 23,3 22,6 L22,15 L24,9 C25,6 28,7 27,10 L26,21 C25,24 23,25 23,28 Z");
        var folds = Geometry.Parse(closed
            ? "M10,12 L10,17 M14,11 L14,15 M18,11 L18,15 M22,12 L22,16 M10,19 C14,17 19,18 21,21"
            : "M12,18 C15,16 21,17 23,19");
        var images = new[] { 32, 48, 64 }.Select(size => (Size: size, Data: Render(size, outline, folds))).ToArray();
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

    private static byte[] Render(int size, Geometry outline, Geometry folds)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(size / 32d, size / 32d));
            var edge = new Pen(Brushes.Black, 1.4) { LineJoin = PenLineJoin.Round };
            drawing.DrawGeometry(Brushes.White, edge, outline);
            drawing.DrawGeometry(null, new Pen(Brushes.Black, 1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, folds);
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
