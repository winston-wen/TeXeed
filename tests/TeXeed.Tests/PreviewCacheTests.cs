using TeXeed.Core;

namespace TeXeed.Tests;

public static partial class PreviewCacheChecks
{
    public static async Task RunAsync(TexProject project, AppSettings settings)
    {
        var service = new CompileService();
        var original = await File.ReadAllTextAsync(project.MainPath);
        var modified = File.GetLastWriteTimeUtc(project.MainPath);
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: true });
        await File.WriteAllTextAsync(project.MainPath, original.Replace("Source", "source"));
        File.SetLastWriteTimeUtc(project.MainPath, modified);
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: false }, "Same-timestamp source edits must invalidate content-based cache");
        await File.WriteAllTextAsync(project.MainPath, original);
        File.SetLastWriteTimeUtc(project.MainPath, modified);
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: true }, "Undoing a source edit should allow reuse of identical output");
        Check.True((await service.GetCachedPreviewAsync(project, settings with { Engine = TexEngine.XeLaTeX })) is { IsCurrent: false }, "Changing engines must invalidate cache");
        Check.True((await service.GetCachedPreviewAsync(project, settings with { FontSize = 24, ThemeId = "github-light-default" })) is { IsCurrent: true }, "UI-only preferences must not require recompilation");
        var asset = Path.Combine(project.Root, "dynamic-image.dat");
        await File.WriteAllTextAsync(asset, "image-one");
        var build = Path.Combine(project.Root, ".texeed", "build");
        var job = Path.GetFileNameWithoutExtension(project.MainPath);
        await File.WriteAllTextAsync(Path.Combine(build, "fake-recorder-input"), asset);
        await service.CompileAsync(project, settings);
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: true }, "Recorder-discovered assets must be cached");
        await File.WriteAllTextAsync(asset, "image-two");
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: false }, "Dynamic image changes must invalidate cache");
        await File.WriteAllTextAsync(asset, "image-one");
        var replacementAsset = Path.Combine(project.Root, "replacement-image.dat");
        await File.WriteAllTextAsync(replacementAsset, "replacement");
        await File.WriteAllTextAsync(Path.Combine(build, "fake-recorder-input"), replacementAsset);
        await service.CompileAsync(project, settings);
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: true }, "Replacing a dependency must create a reusable cache on the first build");
        await File.WriteAllTextAsync(asset, "unused-image");
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: true }, "A removed dependency must no longer invalidate the cache");
        var preview = Path.Combine(project.Root, ".texeed", "preview");
        var pdf = Path.Combine(preview, job + ".pdf");
        var pdfText = await File.ReadAllTextAsync(pdf);
        await File.AppendAllTextAsync(pdf, "tampered");
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: false }, "Modified PDF must not be marked current");
        await File.WriteAllTextAsync(pdf, pdfText);
        var manifest = Path.Combine(preview, job + ".texeed-cache.json");
        var json = await File.ReadAllTextAsync(manifest);
        await File.WriteAllTextAsync(manifest, "broken JSON");
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: false }, "Broken manifest should retain old PDF as a stale preview");
        File.Delete(manifest);
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: false }, "Legacy PDF must be displayable without a manifest");
        await File.WriteAllTextAsync(manifest, json);
        var legacyPdf = Path.Combine(project.Root, "main document.pdf");
        File.Copy(pdf, legacyPdf);
        File.Move(pdf, pdf + ".bak");
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: false } fallback && fallback.PdfPath == legacyPdf,
            "Locally compiled output from another editor must be shown without overwriting it");
        File.Move(pdf + ".bak", pdf);
        File.Delete(legacyPdf);
        Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", "failure");
        await service.CompileAsync(project, settings);
        Check.True((await service.GetCachedPreviewAsync(project, settings)) is { IsCurrent: false }, "Failed build must invalidate the old current marker but preserve its preview");
        Environment.SetEnvironmentVariable("TEXEED_TEST_FAKE_ENGINE", "success");
        await service.CompileAsync(project, settings);
        Console.WriteLine("PASS: persistent PDF cache, content changes with preserved timestamps, dynamic assets, engine/UI preferences, corrupt outputs/manifest and failed-build fallback");
    }
}
