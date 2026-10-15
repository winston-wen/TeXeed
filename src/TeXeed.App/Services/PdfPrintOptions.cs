using System.Globalization;
using System.Windows;

namespace TeXeed.App.Services;

internal enum PdfPrintMode { Single, Multiple, Booklet, Poster }
internal enum PdfPrintScale { Fit, Actual, Shrink, Custom }

// A snapshot is passed to preview and spooler; editing the window never changes a running job.
internal sealed record PdfPrintOptions
{
    internal PdfPrintMode Mode { get; init; }
    internal PdfPrintScale ScaleMode { get; init; }
    internal double Scale { get; init; } = 100;
    internal int Orientation { get; init; } // 0 auto, 1 portrait, 2 landscape
    internal int NUp { get; init; } = 4;
    internal bool ColumnOrder { get; init; }
    internal bool PageBorder { get; init; } = true;
    internal bool RightBinding { get; init; }
    internal int BookletSide { get; init; } // both, front, back
    internal double PosterScale { get; init; } = 180;
    internal double OverlapMm { get; init; } = 5;
    internal bool CutMarks { get; init; } = true;
    internal bool Gray { get; init; }
    internal bool Monochrome { get; init; }
    internal PdfPrintContent Content { get; init; }
    internal int Dpi { get; init; } = 300;
    internal bool SourcePaper { get; init; }
    internal Thickness? Margins { get; init; }
    internal Rect? ViewRegion { get; init; } // PDF points, uncompressed displayed page coordinates
}

internal static class PdfPrintPages
{
    internal static int[] Parse(string text, int count)
    {
        var result = new List<int>(); var seen = new HashSet<int>();
        foreach (var part in text.Replace('，', ',').Replace('、', ',').Split(',', StringSplitOptions.TrimEntries))
        {
            var ends = part.Replace('–', '-').Replace('—', '-').Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length is < 1 or > 2 || !int.TryParse(ends[0], out var from) || from < 1 || from > count ||
                !int.TryParse(ends[^1], out var to) || to < 1 || to > count)
                throw new ArgumentException($"请输入 1–{count} 内的页码，例如 1, 3, 5-8。");
            var step = from <= to ? 1 : -1;
            for (var n = from; ; n += step) { if (seen.Add(n - 1)) result.Add(n - 1); if (n == to) break; }
        }
        return result.ToArray();
    }

    internal static int[] Filter(IEnumerable<int> pages, int parity, bool reverse)
    {
        var result = pages.Where(p => parity == 0 || (p + 1) % 2 == (parity == 1 ? 1 : 0)).ToArray();
        if (reverse) Array.Reverse(result);
        return result;
    }

    internal static string Describe(IReadOnlyList<int> pages, int parity = 0)
    {
        var parts = new List<string>();
        for (var i = 0; i < pages.Count;)
        {
            var end = i; var step = i + 1 < pages.Count ? pages[i + 1] - pages[i] : 0;
            if (Math.Abs(step) is 1 or 2)
                while (end + 1 < pages.Count && pages[end + 1] - pages[end] == step) ++end;
            if (end - i >= 2)
            {
                parts.Add($"{pages[i] + 1}-{pages[end] + 1}" + (Math.Abs(step) == 2 && parity == 0 ? ((pages[i] + 1) % 2 == 1 ? "（奇数）" : "（偶数）") : ""));
                i = end + 1;
            }
            else parts.Add((pages[i++] + 1).ToString(CultureInfo.InvariantCulture));
        }
        return (parity == 1 ? "（仅奇数）" : parity == 2 ? "（仅偶数）" : "") + (parts.Count == 0 ? "无可打印页面" : string.Join("、", parts));
    }
}
