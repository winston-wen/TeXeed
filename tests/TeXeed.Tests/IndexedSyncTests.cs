using TeXeed.Core;

namespace TeXeed.Tests;

public static class IndexedSyncChecks
{
    public static async Task RunAsync(TexProject project)
    {
        var directory = Path.Combine(project.Root, "indexed-sync"); Directory.CreateDirectory(directory);
        var pdf = Path.Combine(directory, "document.pdf"); await File.WriteAllTextAsync(pdf, "%PDF-1.4\n");
        var syncPath = Path.ChangeExtension(pdf, ".synctex");
        var settings = new AppSettings { SynctexPath = Path.Combine(project.Root, "absent-synctex.exe") };
        string Data(string records) => $"SyncTeX Version:1\nInput:1:{project.MainPath}\nOutput:pdf\nMagnification:1000\nUnit:1\nX Offset:0\nY Offset:0\nContent:\n{{1\n(1,12:0,6578176:13156352,657818,0\n{records}\n)\n}}1\nPostamble:\nCount:8\n";
        await File.WriteAllTextAsync(syncPath, Data("x1,14,1:19734528,6578176\nx1,12,1:6578176,6578176"));
        var sync = new SyncTexService(); await sync.PrepareAsync(pdf);
        var tie = await sync.ForwardViewportAsync(project, pdf, new(project.MainPath, 13, 1), settings);
        Check.True(tie is not null && Math.Abs(tie.X - 300) < .01, "Line index must preserve original record order for equal-distance matches");
        await File.WriteAllTextAsync(syncPath, Data("x1,12,9999999:6578176,6578176\nx1,13,1:13156352,6578176\nx1,15,1:19734528,6578176"));
        File.SetLastWriteTimeUtc(syncPath, DateTime.UtcNow.AddSeconds(1));
        var nearby = await sync.ForwardViewportAsync(project, pdf, new(project.MainPath, 12, 1), settings);
        Check.True(nearby is not null && Math.Abs(nearby.X - 200) < .01, "Indexed search must retain weighted column/line matching and invalidate changed maps");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await new SyncTexService().PrepareAsync(pdf, canceled.Token);
        var fresh = await new SyncTexService().ForwardViewportAsync(project, pdf, new(project.MainPath, 12, 1), settings);
        Check.True(fresh == nearby, "Canceled warmup must not poison a future cold lookup");
        Console.WriteLine("PASS: prewarmed indexed SyncTeX preserves nearest/tie/column matching, changed-map invalidation and canceled warmup recovery");
    }
}
