using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeXeed.App.AI;

public sealed record AiSourceBlock(string File, int StartLine, int EndLine, int Offset, string Text, string Revision);
public sealed record AiPdfBlock(int Page, double X, double Y, double Width, double Height, string ImageBase64);
public sealed class AiEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "问题";
    public string Question { get; set; } = "";
    public bool Selected { get; set; } = true;
    public string State { get; set; } = "待提交";
    public List<AiSourceBlock> Sources { get; set; } = [];
    public List<AiPdfBlock> PdfBlocks { get; set; } = [];
    public string PdfRevision { get; set; } = "";
}
public sealed record AiTurn(string Question, string Answer);
public sealed class AiNotebook
{
    public List<AiEntry> Entries { get; set; } = [];
    public List<AiTurn> Turns { get; set; } = [];
}
internal static class AiStorage
{
    internal static string Home => Path.Combine(Environment.GetEnvironmentVariable("TEXEED_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeXeed"), "ai");
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string NotebookPath(string project) => Path.Combine(Home, "projects", Hash(Path.GetFullPath(project).ToUpperInvariant()) + ".json");
    internal static AiNotebook Load(string project)
    {
        var path = NotebookPath(project);
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<AiNotebook>(File.ReadAllText(path)) ?? new();
    }
    internal static void Save(string project, AiNotebook book)
    {
        var path = NotebookPath(project); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(book)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
