using System.IO;
using TeXeed.App.AI;

internal static partial class Program
{
    private static void CheckAiDocumentAssets(string output)
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var root = Path.Combine(output, "asset-fixture-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "figures"); Directory.CreateDirectory(folder);
        var image = Path.Combine(folder, "图 1.png"); byte[] expected = [137, 80, 78, 71, 13, 10, 26, 10]; File.WriteAllBytes(image, expected);
        var uri = new Uri("https://texeed-project.invalid/1/figures/%E5%9B%BE%201.png");
        Check(AiDocumentAssets.TryReadImage(root, "1", uri, out var bytes, out var mime) && mime == "image/png" && bytes.SequenceEqual(expected), "Session-local image was not resolved");
        Check(AiDocumentAssets.TryReadImage(root, "1", new Uri("https://texeed-project.invalid/1/figures/%E5%9B%BE%201"), out bytes, out _) && bytes.SequenceEqual(expected), "Extensionless TeX image path was not resolved");
        Check(!AiDocumentAssets.TryReadImage(root, "1", new Uri("https://outside.invalid/1/figures/a.png"), out _, out _), "External host was accepted");
        Check(!AiDocumentAssets.TryReadImage(root, "1", new Uri("https://texeed-project.invalid/1/figures/%E5%9B%BE%201.png?x=1"), out _, out _), "Query string was accepted");
        Check(!AiDocumentAssets.TryReadImage(root, "1", new Uri("https://texeed-project.invalid/1/%2e%2e%2fsecret.png"), out _, out _), "Encoded traversal was accepted");
        Check(!AiDocumentAssets.TryReadImage(root, "1", new Uri("https://texeed-project.invalid/1/figures%20/%E5%9B%BE%201.png"), out _, out _), "A path component ending in a space was accepted");
        Check(!AiDocumentAssets.TryReadImage(root, "1", new Uri("https://texeed-project.invalid/1/figures./%E5%9B%BE%201.png"), out _, out _), "A path component ending in a dot was accepted");
        Check(!AiDocumentAssets.TryReadImage(root, "1", new Uri("https://texeed-project.invalid/1/%2egit/config.png"), out _, out _), "Git internals were accepted");
        var outside = Path.Combine(output, "outside-" + Guid.NewGuid().ToString("N") + ".png"); File.WriteAllBytes(outside, expected);
        var link = Path.Combine(folder, "linked.png");
        try
        {
            File.CreateSymbolicLink(link, outside);
            Check(!AiDocumentAssets.TryReadImage(root, "1", new Uri("https://texeed-project.invalid/1/figures/linked.png"), out _, out _), "A symbolic link escaped the session directory");
            var linkedRoot = Path.Combine(output, "linked-session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateSymbolicLink(linkedRoot, root);
            Check(!AiDocumentAssets.TryReadImage(linkedRoot, "1", uri, out _, out _), "A symbolic-link session root was accepted");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { Console.WriteLine("SKIP symbolic-link check: " + e.GetType().Name); }
        File.WriteAllText(Path.Combine(root, "references.bib"), "sensitive");
        Check(!AiDocumentAssets.TryReadImage(root, "1", new Uri("https://texeed-project.invalid/1/references.bib"), out _, out _), "Non-image source was exposed to image requests");
        using (var large = File.Create(Path.Combine(folder, "large.png"))) large.SetLength(20 * 1024 * 1024 + 1);
        Check(!AiDocumentAssets.TryReadImage(root, "1", new Uri("https://texeed-project.invalid/1/figures/large.png"), out _, out _), "Oversized image was accepted");
        Console.WriteLine("PASS session-local images, extension lookup, origin/path restrictions and size limit");
    }
}
