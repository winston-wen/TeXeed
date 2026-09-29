using System.IO.Compression;
using System.Text;
using TeXeed.Core;

namespace TeXeed.Tests;

public static class LayoutTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "texeed-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var pdf = Path.Combine(directory, "main.pdf");
            var metadata = Path.ChangeExtension(pdf, TexPageLayout.Extension);
            var sync = Path.ChangeExtension(pdf, ".synctex");
            const string page = "{1\n[1,1:0,0:13156352,19734528,0\n[1,1:2631270,0:7893811,19734528,0\n(1,1:6000000,0:100,100,0\n)\n]\n]\n}1\n";
            const string text = "SyncTeX Version:1\nMagnification:1000\nUnit:1\nX Offset:0\nContent:\n" + page + "Postamble:\nPost Scriptum:\n";
            File.WriteAllText(sync, text);
            var layout = TexPageLayout.Read(pdf).Single();
            Check.True(Math.Abs(layout.Left - 40) < .001 && Math.Abs(layout.Width - 120) < .001, "Read the declared page container, not the tiny indented child");
            File.WriteAllText(sync, text.Replace("Post Scriptum:\n", "Post Scriptum:\nX Offset:10bp\n"));
            Check.Equal(0, TexPageLayout.Read(pdf).Count);
            File.WriteAllText(sync, text.Replace("]\n]\n}1", "]\n[1,1:0,0:100,100,0\n]\n]\n}1"));
            Check.Equal(0, TexPageLayout.Read(pdf).Count);
            File.Delete(sync);
            using (var file = File.Create(sync + ".gz"))
            using (var gzip = new GZipStream(file, CompressionMode.Compress)) gzip.Write(Encoding.UTF8.GetBytes(text));
            Check.Equal(1, TexPageLayout.Read(pdf).Count);
            File.WriteAllBytes(sync + ".gz", [1, 2, 3, 4, 5]);
            Check.Equal(0, TexPageLayout.Read(pdf).Count);
            File.WriteAllText(metadata, "TeXeed layout 1\n13156352:19734528:1315635:7893811\n");
            Check.True(Math.Abs(TexPageLayout.Read(pdf).Single().Left - 20) < .001, "Explicit layout must take priority over SyncTeX");
            File.WriteAllText(metadata, "TeXeed layout 1\n13156352:19734528:9000000:7893811\n");
            Check.Equal(0, TexPageLayout.Read(pdf).Count);
            Console.WriteLine("PASS: declared layout bounds, compressed SyncTeX, malformed/ambiguous metadata and conservative fallback");
        }
        finally { Directory.Delete(directory, true); }
    }
}
