using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using TeXeed.App.Services;

namespace TeXeed.App.Controls;

/// <summary>Frozen vector drawings converted from the locally installed vscode-icons theme.</summary>
public sealed class ProjectFileIcon : FrameworkElement
{
    private static readonly ResourceDictionary Icons = new() { Source = new Uri("/TeXeed;component/ExplorerIcons.xaml", UriKind.Relative) };
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(ProjectFileIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsFolderProperty = DependencyProperty.Register(nameof(IsFolder), typeof(bool), typeof(ProjectFileIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(ProjectFileIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public string FilePath { get => (string)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public bool IsFolder { get => (bool)GetValue(IsFolderProperty); set => SetValue(IsFolderProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    internal string Kind => IsFolder ? IsExpanded ? "folder-open" : "folder-closed" : Path.GetExtension(FilePath ?? "").ToLowerInvariant();
    internal string ResourceName => IsFolder ? IsExpanded ? "default_folder_opened" : "default_folder"
        : Path.GetFileName(FilePath ?? "").Equals(".gitignore", StringComparison.OrdinalIgnoreCase) ? "file_type_git"
        : Kind switch
        {
            ".tex" or ".ltx" or ".bib" => ThemeManager.Current.IsDark ? "file_type_tex" : "file_type_light_tex",
            ".md" or ".rst" => "file_type_markdown", ".pdf" => "file_type_pdf2",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" => "file_type_image",
            ".json" => "file_type_json", ".cs" => "file_type_csharp", ".txt" => "file_type_text",
            ".sty" or ".cls" or ".cfg" or ".toml" or ".yaml" or ".yml" => "file_type_config", _ => "default_file"
        };
    static ProjectFileIcon() => TextElement.ForegroundProperty.OverrideMetadata(typeof(ProjectFileIcon),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public ProjectFileIcon() { Width = 18; Height = 18; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc) => dc.DrawImage((ImageSource)Icons[ResourceName], new Rect(RenderSize));
}
