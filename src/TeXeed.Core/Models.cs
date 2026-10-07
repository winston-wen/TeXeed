namespace TeXeed.Core;

public enum ProjectFileKind { Tex, Bibliography, Style, Asset }
public sealed record ProjectFile(string Path, string RelativePath, ProjectFileKind Kind, string Content)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsEditable => Kind != ProjectFileKind.Asset;
}
public sealed record TexProject(string Root, string MainPath, IReadOnlyList<ProjectFile> Files, IReadOnlyList<string> Warnings)
{
    public string? ConfiguredName { get; init; }
    public string Name => string.IsNullOrWhiteSpace(ConfiguredName) ? new System.IO.DirectoryInfo(Root).Name : ConfiguredName;
    public string DocumentTitle => LatexTitle.PlainText(LatexSyntax.Commands(LatexSyntax.Analyze(
        Files.FirstOrDefault(f => string.Equals(f.Path, MainPath, StringComparison.OrdinalIgnoreCase))?.Content ?? "").Code)
        .FirstOrDefault(c => c.Name == "title" && c.Arguments.Count > 0)?.Arguments[0] ?? "");
}
public sealed record SourceLocation(string Path, int Line, int Column = 1);
public sealed record IndexEntry(string Title, string Path, int Line, string Detail = "", int Level = 0)
{
    public string? Latex { get; init; }
    public string Location => $"{System.IO.Path.GetFileName(Path)}:{Line}";
}
public sealed record ProjectIndex(IReadOnlyList<IndexEntry> Outline, IReadOnlyList<IndexEntry> Labels, IReadOnlyList<IndexEntry> Citations, IReadOnlyList<IndexEntry> Todos);
public enum TexEngine { Auto, Latexmk, XeLaTeX, PdfLaTeX, LuaLaTeX, Tectonic }
public enum PreviewFollowMode { None = 0, Cursor = 1, Viewport = 2 }
public sealed record AppSettings
{
    public string ThemeId { get; init; } = "ristretto";
    public TexEngine Engine { get; init; } = TexEngine.Auto;
    public string EnginePath { get; init; } = "";
    public string SynctexPath { get; init; } = "";
    public bool LivePreview { get; init; } = true;
    public PreviewFollowMode FollowMode { get; init; } = PreviewFollowMode.Viewport;
    public bool InvertPdfColors { get; init; } = true;
    public bool AutoCropPdf { get; init; } = true;
    public int DebounceMs { get; init; } = 1000;
    public double FontSize { get; init; } = 15;
    public string EditorFontFamily { get; init; } = "Cascadia Code, Consolas";
    public bool WordWrap { get; init; } = true;
    public string[] RecentProjects { get; init; } = [];
}
public sealed record Diagnostic(string Message, string Path, int Line, bool IsWarning = false);
public sealed record CompileResult(bool Success, string? PdfPath, string Log, IReadOnlyList<Diagnostic> Diagnostics, TimeSpan Duration, string Engine, bool SynctexAvailable);
public sealed record PdfLocation(int Page, double X, double Y, double Width = 0, double Height = 0);
public sealed record ToolInfo(string Name, string Path);
public sealed record Toolchain(IReadOnlyList<ToolInfo> Engines, string? SynctexPath);
