using System.Globalization;
using System.IO.Compression;

namespace TeXeed.Core;

/// <summary>Physical page layout recorded by TeX at shipout, in PDF points.</summary>
public sealed record TexPageLayout(double PaperWidth, double PaperHeight, double Left, double Width)
{
    public const string Extension = ".texeed-layout";

    public static IReadOnlyList<TexPageLayout> Read(string pdfPath)
    {
        try
        {
            var path = Path.ChangeExtension(pdfPath, Extension);
            if (!File.Exists(path)) return ReadSyncTex(pdfPath);
            if (new FileInfo(path).Length > 2_000_000) return [];
            var lines = File.ReadAllLines(path);
            if (lines.Length < 1 || lines[0] != "TeXeed layout 1") return [];
            var result = new List<TexPageLayout>();
            foreach (var line in lines.Skip(1))
            {
                var fields = line.Split(':');
                if (fields.Length != 4) return [];
                var values = new double[4];
                for (var i = 0; i < 4; i++)
                    if (!double.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]) ||
                        !double.IsFinite(values[i])) return [];
                // TeX scaled points -> PDF big points; \mag is applied by the recorder.
                var layout = new TexPageLayout(values[0] / 65781.76, values[1] / 65781.76, values[2] / 65781.76, values[3] / 65781.76);
                if (layout.PaperWidth <= 0 || layout.PaperHeight <= 0 || layout.Width <= 0 || layout.Left < 0 ||
                    layout.Left + layout.Width > layout.PaperWidth + 1 || result.Count >= 10000) return [];
                result.Add(layout);
            }
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return []; }
    }

    // The standard LaTeX output routine wraps header/body/footer in a single vertical
    // page container. Its declared width is \textwidth even on indented, short or empty lines.
    // Never derive margins from leaf boxes, glyphs, or the union of visible marks.
    private static IReadOnlyList<TexPageLayout> ReadSyncTex(string pdfPath)
    {
        var plain = Path.ChangeExtension(pdfPath, ".synctex");
        var path = new[] { plain, plain + ".gz" }.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (path is null || new FileInfo(path).Length > 64 * 1024 * 1024) return [];
        using var file = File.OpenRead(path);
        using var stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? (Stream)new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream);
        if (reader.ReadLine() != "SyncTeX Version:1") return [];
        var pages = new List<(double X, double Width)>();
        double unit = 8192, magnification = 1000, xOffset = 578;
        var stack = new Stack<char>();
        var page = 0; var candidates = 0; var count = 0; var complete = false;
        (double X, double Width) box = default;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            count += line.Length + 1;
            if (count > 64 * 1024 * 1024) return [];
            if (line.StartsWith("Unit:")) { if (!Number(line[5..], out unit)) return []; }
            else if (line.StartsWith("Magnification:")) { if (!Number(line[14..], out magnification)) return []; }
            else if (line.StartsWith("X Offset:")) { if (!Number(line[9..], out xOffset)) return []; }
            else if (line.StartsWith("Postamble:")) complete = true;
            // Modified post-processing coordinates or PDF forms require the full SyncTeX parser.
            else if (line.StartsWith("Post Scriptum:"))
            {
                while ((line = reader.ReadLine()) is not null)
                    if (!string.IsNullOrWhiteSpace(line) || (count += line.Length + 1) > 64 * 1024 * 1024) return [];
                break;
            }
            else if (line.StartsWith('{'))
            {
                if (!int.TryParse(line.AsSpan(1), out page) || page != pages.Count + 1 || page > 10000) return [];
                stack.Clear(); candidates = 0; box = default;
            }
            else if (line.StartsWith('}'))
            {
                if (page == 0 || candidates != 1 || stack.Count != 0) return [];
                pages.Add(box); page = 0;
            }
            else if (page > 0 && line.Length > 0)
            {
                if (line[0] is '[' or '(')
                {
                    if (line[0] == '[' && stack.Count == 1 && stack.Peek() == '[')
                    {
                        var fields = line[1..].Split(':');
                        if (fields.Length != 3 || !Number(fields[1].Split(',')[0], out var x) ||
                            !Number(fields[2].Split(',')[0], out var width) || width <= 0) return [];
                        box = (x, width); candidates++;
                    }
                    stack.Push(line[0]);
                }
                else if (line[0] is ']' or ')')
                {
                    if (!stack.TryPop(out var opening) || opening != (line[0] == ']' ? '[' : '(')) return [];
                }
                else if (line[0] is '<' or '>') return [];
            }
        }
        if (!complete || page != 0 || unit <= 0 || magnification <= 0) return [];
        var scale = unit / 65781.76 * magnification / 1000;
        // Paper size is supplied by the PDF when layout comes from SyncTeX.
        return pages.Select(b => new TexPageLayout(0, 0, b.X * scale + xOffset * unit / 65781.76, b.Width * scale)).ToArray();
    }

    private static bool Number(string value, out double number) =>
        double.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
}
