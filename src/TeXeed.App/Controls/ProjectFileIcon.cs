using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace TeXeed.App.Controls;

/// <summary>Small native vector icons for the project explorer; no shell extraction or executable loading.</summary>
public sealed class ProjectFileIcon : FrameworkElement
{
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(ProjectFileIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsFolderProperty = DependencyProperty.Register(nameof(IsFolder), typeof(bool), typeof(ProjectFileIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(ProjectFileIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public string FilePath { get => (string)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public bool IsFolder { get => (bool)GetValue(IsFolderProperty); set => SetValue(IsFolderProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    internal string Kind => IsFolder ? IsExpanded ? "folder-open" : "folder-closed" : Path.GetExtension(FilePath ?? "").ToLowerInvariant();
    public ProjectFileIcon() { Width = 18; Height = 18; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        Brush Color(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        void Shape(string data, Brush fill) => dc.DrawGeometry(fill, null, Geometry.Parse(data));
        var gold = Color("#C8AB71");
        if (IsFolder)
        {
            Shape("M1,4 H7 L9,2 H17 V15 H1 Z", gold);
            if (IsExpanded) Shape("M1,7 H18 L15,16 H0 Z", Color("#E0C994"));
            else dc.DrawLine(new Pen(Color("#2C2525"), 1), new Point(2, 6), new Point(16, 6));
            return;
        }
        var extension = Kind;
        var name = Path.GetFileName(FilePath ?? "");
        if (name == ".gitignore" || extension == ".git")
        {
            Shape("M9,0 L18,9 9,18 0,9 Z", Color("#E2694A"));
            dc.DrawLine(new Pen(Brushes.White, 1.4), new Point(6, 5), new Point(12, 11));
            dc.DrawLine(new Pen(Brushes.White, 1.4), new Point(8, 7), new Point(8, 13));
            foreach (var point in new[] { new Point(6, 5), new Point(12, 11), new Point(8, 13) }) dc.DrawEllipse(Brushes.White, null, point, 1.4, 1.4);
            return;
        }
        var (color, label) = extension switch
        {
            ".tex" or ".ltx" => ("#83BC9C", "T"),
            ".bib" => ("#D8B66D", "B"),
            ".sty" or ".cls" or ".def" => ("#BE9AD4", "{}"),
            ".md" or ".rst" => ("#C7A56C", "M↓"),
            ".pdf" => ("#E47B72", "PDF"),
            ".png" or ".jpg" or ".jpeg" or ".svg" or ".gif" or ".webp" => ("#BA9BD5", "▧"),
            ".json" or ".yaml" or ".yml" or ".toml" => ("#D6B86C", "{}"),
            ".py" or ".ps1" or ".cs" or ".js" or ".lua" => ("#84B6DC", "<>"),
            ".zip" or ".7z" or ".tar" or ".gz" => ("#CCAD7A", "≡"),
            _ => ("#A8ACB1", "")
        };
        var brush = Color(color);
        dc.DrawGeometry(null, new Pen(brush, 1.2), Geometry.Parse("M3,1 H11 L16,6 V17 H3 Z M11,1 V6 H16"));
        if (label.Length == 0) return;
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), label.Length > 1 ? 7 : 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point((18 - text.Width) / 2, 6));
    }
}
