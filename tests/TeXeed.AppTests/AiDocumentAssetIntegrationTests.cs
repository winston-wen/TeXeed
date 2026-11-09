using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static int RunAiDocumentAssetsIntegration(string output)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TeXeed;component/Theme.xaml", UriKind.Relative) });
        var result = 1;
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); e.Handled = true; app.Shutdown(1); };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckAiDocumentAssetsIntegration(output); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        }), DispatcherPriority.ApplicationIdle);
        app.Run(); return result;
    }

    private static async Task CheckAiDocumentAssetsIntegration(string output)
    {
        static void Image(string path, int width)
        {
            var pixels = new byte[width * 4];
            for (var i = 0; i < width; i++) { pixels[i * 4] = 255; pixels[i * 4 + 3] = 255; }
            var bitmap = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path); encoder.Save(file);
        }
        var fixture = Path.Combine(output, "asset-webview-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(fixture, "one"); var b = Path.Combine(fixture, "two");
        Directory.CreateDirectory(Path.Combine(a, "figures")); Directory.CreateDirectory(Path.Combine(b, "figures"));
        Image(Path.Combine(a, "figures", "a.png"), 1); Image(Path.Combine(b, "figures", "a.png"), 2);
        File.WriteAllText(Path.Combine(a, "secret.bib"), "not an image");
        var source = "\\documentclass{article}\n\\usepackage{graphicx}\n\\begin{document}\n\\includegraphics{figures/a.png}\n\\end{document}";
        var document = new AiSharedDocument();
        if (!document.TryInitialize(source, "model", "ai")) throw new InvalidOperationException("Unable to initialize fixture");
        var view = new AiDocumentView();
        var host = new Window { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Width = 1000, Height = 800, ShowInTaskbar = false, Content = view };
        view.Bind(document, "资源测试", "用户", "model", a);
        host.Show();
        try
        {
            var browser = Field(view, "_browser")!;
            object? Core() => browser.GetType().GetProperty("CoreWebView2")!.GetValue(browser);
            await EventuallyAsync(() => Task.FromResult(Core() is not null), "WebView2 did not initialize");
            async Task<string> Script(string script) => await (Task<string>)Core()!.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(Core(), [script])!;
            await EventuallyAsync(async () => await Script("document.querySelector('.mentor-image img')?.naturalWidth || 0") == "1", "Session image did not load through the real WebView resource bridge");
            view.Bind(document, "资源测试", "用户", "model", b);
            await EventuallyAsync(async () => await Script("document.querySelector('.mentor-image img')?.naturalWidth || 0") == "2", "Changing session directory reused another session's image");
            view.Bind(document, "资源测试", "用户", "model", a);
            await EventuallyAsync(async () => await Script("document.querySelector('.mentor-image img')?.naturalWidth || 0") == "1", "Switching back did not reload the original session image");
            await Script("(()=>{window.assetProbe='pending';const i=new Image();i.onload=()=>window.assetProbe='loaded';i.onerror=()=>window.assetProbe='blocked';i.src=document.querySelector('.mentor-image img').src.replace('figures/a.png', 'secret.bib');})()");
            await EventuallyAsync(async () => await Script("window.assetProbe") == "\"blocked\"", "Non-image data was not blocked by the resource bridge");
            Console.WriteLine("PASS actual WebView session-local image loading, session isolation and non-image blocking");
        }
        finally { host.Close(); }
    }
}
