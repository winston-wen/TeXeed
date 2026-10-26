using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace TeXeed.App.Controls;

public sealed partial class LatexEditor
{
    // Cache shaped graphemes, not document lines: scrolling/jumping can reuse glyphs
    // while each visual line keeps its own highlighting, selection and caret mapping.
    private readonly Dictionary<GridGlyphKey, GridGlyph> _gridGlyphs = [];
    private readonly record struct GridGlyphKey(string Text, int Cells, double CellWidth,
        Typeface Typeface, double Size, string Culture, double Dpi, Color? Color,
        double Opacity, Brush? Brush, TextDecorationCollection? Decorations);
    private sealed record GridGlyph(DrawingGroup Drawing, double Height, double Baseline);

    private GridGlyph GetGridGlyph(string text, int cells, double cellWidth, TextRunProperties properties, TextView view)
    {
        var brush = properties.ForegroundBrush;
        var solid = brush as SolidColorBrush;
        var decorations = properties.TextDecorations is { Count: > 0 } ? properties.TextDecorations : null;
        var cacheable = (solid is not null || brush.IsFrozen) && (decorations is null || decorations.IsFrozen);
        var culture = properties.CultureInfo ?? CultureInfo.CurrentUICulture;
        var dpi = VisualTreeHelper.GetDpi(view).PixelsPerDip;
        var key = new GridGlyphKey(text, cells, cellWidth, properties.Typeface, properties.FontRenderingEmSize,
            culture.Name, dpi, solid?.Color, brush.Opacity, solid is null ? brush : null, decorations);
        if (cacheable && _gridGlyphs.TryGetValue(key, out var cached)) return cached;

        // Own the drawing resources; freezing a cached drawing must never freeze a
        // brush supplied by the editor or a highlighting rule.
        var foreground = brush.CloneCurrentValue();
        var shaped = new FormattedText(text, culture, FlowDirection.LeftToRight,
            properties.Typeface, properties.FontRenderingEmSize, foreground, dpi);
        if (decorations is not null) shaped.SetTextDecorations(decorations.CloneCurrentValue());
        var ink = shaped.BuildGeometry(new Point()).Bounds;
        var inkLeft = ink.IsEmpty ? 0 : Math.Min(0, ink.Left);
        var inkRight = ink.IsEmpty ? shaped.WidthIncludingTrailingWhitespace : Math.Max(shaped.WidthIncludingTrailingWhitespace, ink.Right);
        var naturalWidth = Math.Max(0.001, inkRight - inkLeft);
        var width = cells * cellWidth;
        var scale = Math.Min(1, width / naturalWidth);
        var left = (width - naturalWidth * scale) / 2 - inkLeft * scale;
        var top = ink.IsEmpty ? 0 : -Math.Min(0, ink.Top);
        var height = Math.Max(shaped.Height, ink.IsEmpty ? 0 : ink.Bottom) + top;
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            context.PushTransform(new TranslateTransform(left, top));
            context.PushTransform(new ScaleTransform(scale, 1));
            context.DrawText(shaped, new Point());
        }
        if (drawing.CanFreeze) drawing.Freeze();
        var glyph = new GridGlyph(drawing, height, shaped.Baseline + top);
        if (cacheable && drawing.IsFrozen)
        {
            // Bound retained glyphs across documents, font choices and zoom/DPI changes.
            if (_gridGlyphs.Count >= 4096) _gridGlyphs.Clear();
            _gridGlyphs[key] = glyph;
        }
        return glyph;
    }

    /// <summary>The width of a half-width editing cell, in device-independent pixels.</summary>
    public double GridCellWidth => FontSize / 2;

    /// <summary>Returns the zero-based grid column, expanding tabs and keeping graphemes intact.</summary>
    public int GetGridColumn(int offset)
    {
        if (Document is null) return 0;
        offset = Math.Clamp(offset, 0, Document.TextLength);
        var line = Document.GetLineByOffset(offset);
        var text = Document.GetText(line);
        var column = 0;
        for (var index = 0; index < text.Length && line.Offset + index < offset;)
        {
            var length = StringInfo.GetNextTextElementLength(text, index);
            if (line.Offset + index + length > offset) break;
            column += FixedGridUnicode.CellCount(text.AsSpan(index, length), column, Options.IndentationSize);
            index += length;
        }
        return column;
    }

    /// <summary>Maps a zero-based grid column to a whole-grapheme document boundary.</summary>
    public int GetOffsetAtGridColumn(int lineNumber, int column)
    {
        if (Document is null) return 0;
        var line = Document.GetLineByNumber(Math.Clamp(lineNumber, 1, Document.LineCount));
        var text = Document.GetText(line);
        var current = 0;
        for (var index = 0; index < text.Length;)
        {
            var length = StringInfo.GetNextTextElementLength(text, index);
            var next = current + FixedGridUnicode.CellCount(text.AsSpan(index, length), current, Options.IndentationSize);
            if (column < next) return line.Offset + index;
            current = next;
            index += length;
        }
        return line.EndOffset;
    }

    private void InitializeFixedGrid()
    {
        var generators = TextArea.TextView.ElementGenerators;
        if (generators.OfType<FixedGridGenerator>().Any()) return;
        var index = 0;
        // Folding owns hidden ranges; all ordinary text (including tabs) uses our grid.
        while (index < generators.Count && generators[index] is FoldingElementGenerator) index++;
        generators.Insert(index, new FixedGridGenerator(this));
    }

    private sealed class FixedGridGenerator(LatexEditor editor) : VisualLineElementGenerator
    {
        private DocumentLine? _line;
        private string _text = "";
        private int[] _starts = [], _widths = [];

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _line = null;
        }

        public override int GetFirstInterestedOffset(int startOffset)
            => startOffset < CurrentContext.VisualLine.LastDocumentLine.EndOffset ? startOffset : -1;

        public override VisualLineElement ConstructElement(int offset)
        {
            var line = CurrentContext.Document.GetLineByOffset(offset);
            if (!ReferenceEquals(_line, line))
            {
                _line = line;
                _text = CurrentContext.Document.GetText(line);
                _starts = StringInfo.ParseCombiningCharacters(_text);
                _widths = new int[_starts.Length];
                var column = 0;
                for (var i = 0; i < _starts.Length; i++)
                {
                    var end = i + 1 < _starts.Length ? _starts[i + 1] : _text.Length;
                    _widths[i] = FixedGridUnicode.CellCount(_text.AsSpan(_starts[i], end - _starts[i]), column, editor.Options.IndentationSize);
                    column += _widths[i];
                }
            }
            var start = offset - line.Offset;
            var item = Array.BinarySearch(_starts, start);
            if (item < 0) item = Math.Max(0, ~item - 1);
            var endOffset = item + 1 < _starts.Length ? _starts[item + 1] : _text.Length;
            return new FixedGridElement(editor, CurrentContext.VisualLine, _text[start..endOffset], _widths[item], editor.GridCellWidth);
        }
    }

    private sealed class FixedGridElement(LatexEditor editor, VisualLine line, string text, int cells, double cellWidth)
        : VisualLineElement(cells, text.Length)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
            => new FixedGridRun(editor, text, VisualLength, cellWidth, TextRunProperties, context.TextView);

        public override bool IsWhitespace(int visualColumn) => string.IsNullOrWhiteSpace(text);

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
        {
            // Backspace normally asks for EveryCodepoint; a grid cell still represents
            // a whole grapheme, so never leave a dangling accent, surrogate or ZWJ.
            if (mode is CaretPositioningMode.Normal or CaretPositioningMode.EveryCodepoint)
                return base.GetNextCaretPosition(visualColumn, direction, mode);
            // Object boundaries must not turn Ctrl+Left/Right into single-character movement.
            var start = line.StartOffset + RelativeTextOffset;
            var end = start + DocumentLength;
            var offset = visualColumn < VisualColumn ? start - 1
                : visualColumn >= VisualColumn + VisualLength ? end : start;
            var next = TextUtilities.GetNextCaretPosition(line.Document, offset, direction, mode);
            if (next < start || next > end) return -1;
            if (next == start) return VisualColumn;
            return VisualColumn + VisualLength;
        }
    }

    private sealed class FixedGridRun : TextEmbeddedObject
    {
        private readonly GridGlyph? _glyph;
        private readonly int _length;
        private readonly TextRunProperties _properties;
        private readonly double _width, _height, _baseline;
        private readonly bool _whitespace;

        public FixedGridRun(LatexEditor editor, string text, int cells, double cellWidth, TextRunProperties properties, TextView view)
        {
            _length = cells;
            _properties = properties;
            _width = cells * cellWidth;
            _whitespace = string.IsNullOrWhiteSpace(text);
            if (_whitespace)
            {
                _height = view.DefaultLineHeight;
                _baseline = view.DefaultBaseline;
                return;
            }
            _glyph = editor.GetGridGlyph(text, cells, cellWidth, properties, view);
            _height = _glyph.Height;
            _baseline = _glyph.Baseline;
        }

        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakPossible;
        public override LineBreakCondition BreakAfter => _whitespace ? LineBreakCondition.BreakDesired : LineBreakCondition.BreakPossible;
        public override bool HasFixedSize => true;
        public override CharacterBufferReference CharacterBufferReference => new();
        public override int Length => _length;
        public override TextRunProperties Properties => _properties;
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) => new(_width, _height, _baseline);
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) => new(0, -_baseline, _width, _height);

        public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways)
        {
            if (_glyph is null) return;
            drawingContext.PushTransform(new TranslateTransform(origin.X, origin.Y - _baseline));
            drawingContext.DrawDrawing(_glyph.Drawing);
            drawingContext.Pop();
        }
    }
}

internal static class FixedGridUnicode
{
    internal static int CellCount(ReadOnlySpan<char> grapheme, int column, int tabSize)
    {
        if (grapheme.SequenceEqual("\t")) return Math.Max(1, tabSize) - column % Math.Max(1, tabSize);
        var width = 1;
        foreach (var rune in grapheme.EnumerateRunes())
        {
            // Emoji presentation, keycaps and regional-indicator flags are two cells,
            // including text-default bases such as U+2764 followed by VS16.
            if (rune.Value is 0xFE0F or 0x20E3 or >= 0x1F1E6 and <= 0x1F1FF) return 2;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            if (IsWide(rune.Value)) width = 2;
        }
        return width;
    }

    private static bool IsWide(int codepoint)
    {
        var low = 0;
        var high = WideRanges.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var (start, end) = WideRanges[middle];
            if (codepoint < start) high = middle - 1;
            else if (codepoint > end) low = middle + 1;
            else return true;
        }
        return false;
    }

    // Unicode 17.0 EastAsianWidth W/F ranges, adjacent ranges merged; A stays narrow.
    // https://www.unicode.org/Public/17.0.0/ucd/EastAsianWidth.txt
    private static readonly (int Start, int End)[] WideRanges =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC),
        (0x23F0, 0x23F0), (0x23F3, 0x23F3), (0x25FD, 0x25FE), (0x2614, 0x2615),
        (0x2630, 0x2637), (0x2648, 0x2653), (0x267F, 0x267F), (0x268A, 0x268F),
        (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE),
        (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA),
        (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD),
        (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C),
        (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50),
        (0x2B55, 0x2B55), (0x2E80, 0x2E99), (0x2E9B, 0x2EF3), (0x2F00, 0x2FD5),
        (0x2FF0, 0x303E), (0x3041, 0x3096), (0x3099, 0x30FF), (0x3105, 0x312F),
        (0x3131, 0x318E), (0x3190, 0x31E5), (0x31EF, 0x321E), (0x3220, 0x3247),
        (0x3250, 0xA48C), (0xA490, 0xA4C6), (0xA960, 0xA97C), (0xAC00, 0xD7A3),
        (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE52), (0xFE54, 0xFE66),
        (0xFE68, 0xFE6B), (0xFF01, 0xFF60), (0xFFE0, 0xFFE6), (0x16FE0, 0x16FE4),
        (0x16FF0, 0x16FF6), (0x17000, 0x18CD5), (0x18CFF, 0x18D1E), (0x18D80, 0x18DF2),
        (0x1AFF0, 0x1AFF3), (0x1AFF5, 0x1AFFB), (0x1AFFD, 0x1AFFE), (0x1B000, 0x1B122),
        (0x1B132, 0x1B132), (0x1B150, 0x1B152), (0x1B155, 0x1B155), (0x1B164, 0x1B167),
        (0x1B170, 0x1B2FB), (0x1D300, 0x1D356), (0x1D360, 0x1D376), (0x1F004, 0x1F004),
        (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A), (0x1F200, 0x1F202),
        (0x1F210, 0x1F23B), (0x1F240, 0x1F248), (0x1F250, 0x1F251), (0x1F260, 0x1F265),
        (0x1F300, 0x1F320), (0x1F32D, 0x1F335), (0x1F337, 0x1F37C), (0x1F37E, 0x1F393),
        (0x1F3A0, 0x1F3CA), (0x1F3CF, 0x1F3D3), (0x1F3E0, 0x1F3F0), (0x1F3F4, 0x1F3F4),
        (0x1F3F8, 0x1F43E), (0x1F440, 0x1F440), (0x1F442, 0x1F4FC), (0x1F4FF, 0x1F53D),
        (0x1F54B, 0x1F54E), (0x1F550, 0x1F567), (0x1F57A, 0x1F57A), (0x1F595, 0x1F596),
        (0x1F5A4, 0x1F5A4), (0x1F5FB, 0x1F64F), (0x1F680, 0x1F6C5), (0x1F6CC, 0x1F6CC),
        (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D8), (0x1F6DC, 0x1F6DF), (0x1F6EB, 0x1F6EC),
        (0x1F6F4, 0x1F6FC), (0x1F7E0, 0x1F7EB), (0x1F7F0, 0x1F7F0), (0x1F90C, 0x1F93A),
        (0x1F93C, 0x1F945), (0x1F947, 0x1F9FF), (0x1FA70, 0x1FA7C), (0x1FA80, 0x1FA8A),
        (0x1FA8E, 0x1FAC6), (0x1FAC8, 0x1FAC8), (0x1FACD, 0x1FADC), (0x1FADF, 0x1FAEA),
        (0x1FAEF, 0x1FAF8), (0x20000, 0x2FFFD), (0x30000, 0x3FFFD)
    ];
}
