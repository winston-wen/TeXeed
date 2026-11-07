using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static int RunAiFullDocumentPerformance(string output)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TeXeed;component/Theme.xaml", UriKind.Relative) });
        var result = 1;
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); e.Handled = true; app.Shutdown(1); };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await MeasureAiFullDocumentPerformanceAsync(output); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        }), DispatcherPriority.ApplicationIdle);
        app.Run();
        return result;
    }

    private static async Task MeasureAiFullDocumentPerformanceAsync(string output)
    {
        var view = new AiDocumentView();
        var host = new Window { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Width = 1000, Height = 800, ShowInTaskbar = false, Content = view };
        host.Show();
        try
        {
            var browser = typeof(AiDocumentView).GetField("_browser", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
            object? Core() => browser.GetType().GetProperty("CoreWebView2")!.GetValue(browser);
            await EventuallyAsync(() => Task.FromResult(Core() is not null), "Actual WebView2 did not initialize");
            async Task<string> Script(string script) => await (Task<string>)Core()!.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(Core(), [script])!;
            async Task<string> Protocol(string name, string argument) => await (Task<string>)Core()!.GetType().GetMethod("CallDevToolsProtocolMethodAsync")!.Invoke(Core(), [name, argument])!;
            await EventuallyAsync(async () => await Script("document.readyState") == "\"complete\"", "WebView2 HTML failed to load");
            var paragraphs = Enumerable.Range(1, 120).Select(i => $"第 {i} 段：这个结论需要检查已有假设及推导过程，记录仍未解决的问题。").ToArray();
            var source = "\\documentclass{ctexart}\n\\usepackage{amsmath}\n\\usepackage{graphicx}\n\\begin{document}\n\n\\section{研究记录}\n\n" + string.Join("\n\n", paragraphs)
                + "\n\n\\begin{figure}\n\\includegraphics{figures/sample.png}\n\\end{figure}\n\n\\cite{sample}\n\\bibliography{refs}\n\\end{document}";
            var result = new List<object>();
            for (var round = 0; round < 3; round++)
            foreach (var count in new[] { 0, 60 })
            {
                var document = new AiSharedDocument(); document.TryInitialize(source, "测试模型");
                for (var i = 0; i < count; i++)
                {
                    var quote = $"第 {i + 1} 段"; var start = source.IndexOf(quote, StringComparison.Ordinal);
                    document.Comments.Add(new AiDocumentComment { Id = "perf-" + i, Start = start, Length = quote.Length, Quote = quote, Text = "请核对论证的前提和范围。", Replies = [new AiDocumentReply { Text = "进一步检查支持材料。" }] });
                }
                view.Bind(document, "性能夹具", "用户", "测试模型");
                await EventuallyAsync(async () => await Script("document.querySelector('#mentor-document')?.textContent.includes('第 120 段')") == "true", "Full-document fixture did not render");
                var samples = new List<object>();
                for (var i = 0; i < 9; i++)
                {
                    if (i == 2) await Protocol("HeapProfiler.startSampling", "{\"samplingInterval\":1024,\"includeObjectsCollectedByMajorGC\":true,\"includeObjectsCollectedByMinorGC\":true}");
                    await Script("window.__measure=null; {const start=performance.now(); document.addEventListener('texeed-document-rendered',()=>{const sync=performance.now()-start;requestAnimationFrame(()=>requestAnimationFrame(()=>{window.__measure={sync,frame:performance.now()-start}}))},{once:true});} true");
                    view.RefreshDocument();
                    await EventuallyAsync(async () => await Script("window.__measure!==null") == "true", "Full-document render timing did not complete");
                    using var rendering = JsonDocument.Parse(await Script("window.__measure"));
                    var sync = rendering.RootElement.GetProperty("sync").GetDouble(); var frame = rendering.RootElement.GetProperty("frame").GetDouble();
                    await Script("window.__selection=null; {const p=[...document.querySelectorAll('#mentor-document p')].find(e=>e.textContent.includes('第 1 段')),node=document.createTreeWalker(p,NodeFilter.SHOW_TEXT).nextNode(),r=document.createRange();r.setStart(node,0);r.setEnd(node,Math.min(4,node.length));const start=performance.now(),s=getSelection();s.removeAllRanges();s.addRange(r);const sync=performance.now()-start;requestAnimationFrame(()=>requestAnimationFrame(()=>{window.__selection={sync,frame:performance.now()-start};s.removeAllRanges()}))} true");
                    await EventuallyAsync(async () => await Script("window.__selection!==null") == "true", "Full-document selection timing did not complete");
                    using var selected = JsonDocument.Parse(await Script("window.__selection"));
                    if (i >= 2) samples.Add(new { render = sync, renderFrame = frame, selection = selected.RootElement.GetProperty("sync").GetDouble(), selectionFrame = selected.RootElement.GetProperty("frame").GetDouble() });
                }
                using var heap = JsonDocument.Parse(await Protocol("HeapProfiler.stopSampling", "{}"));
                var profile = heap.RootElement.GetProperty("profile");
                static long Retained(JsonElement node) => (node.TryGetProperty("selfSize", out var size) ? size.GetInt64() : 0) + (node.TryGetProperty("children", out var children) ? children.EnumerateArray().Sum(Retained) : 0);
                var sampled = Retained(profile.GetProperty("head"));
                var allocated = profile.GetProperty("samples").EnumerateArray().Sum(sample => sample.GetProperty("size").GetInt64());
                var allocatedByAction = new Dictionary<string, long>();
                foreach (var action in new[] { "render", "selection" })
                {
                    for (var i = 0; i < 9; i++)
                    {
                        if (i == 2) await Protocol("HeapProfiler.startSampling", "{\"samplingInterval\":1024,\"includeObjectsCollectedByMajorGC\":true,\"includeObjectsCollectedByMinorGC\":true}");
                        if (action == "render")
                        {
                            await Script("window.__measure=null; document.addEventListener('texeed-document-rendered',()=>requestAnimationFrame(()=>requestAnimationFrame(()=>{window.__measure=true})),{once:true}); true");
                            view.RefreshDocument();
                            await EventuallyAsync(async () => await Script("window.__measure===true") == "true", "Full-document allocation render did not complete");
                        }
                        else
                        {
                            await Script("window.__selection=null; {const p=[...document.querySelectorAll('#mentor-document p')].find(e=>e.textContent.includes('第 1 段')),node=document.createTreeWalker(p,NodeFilter.SHOW_TEXT).nextNode(),r=document.createRange();r.setStart(node,0);r.setEnd(node,Math.min(4,node.length));const s=getSelection();s.removeAllRanges();s.addRange(r);requestAnimationFrame(()=>requestAnimationFrame(()=>{s.removeAllRanges();window.__selection=true}))} true");
                            await EventuallyAsync(async () => await Script("window.__selection===true") == "true", "Full-document allocation selection did not complete");
                        }
                    }
                    using var actionHeap = JsonDocument.Parse(await Protocol("HeapProfiler.stopSampling", "{}"));
                    allocatedByAction[action] = actionHeap.RootElement.GetProperty("profile").GetProperty("samples").EnumerateArray().Sum(sample => sample.GetProperty("size").GetInt64());
                }
                using var counts = JsonDocument.Parse(await Script("(()=>{const e=[...document.querySelectorAll('button,input,textarea,select,summary')];return {elements:document.querySelectorAll('*').length,blocks:document.querySelectorAll('#mentor-document .mentor-block').length,paragraphs:document.querySelectorAll('#mentor-document p').length,comments:document.querySelectorAll('.mentor-comment').length,visible:e.filter(x=>{const r=x.getBoundingClientRect();return r.width&&r.height&&r.right>0&&r.left<innerWidth&&r.bottom>0&&r.top<innerHeight}).length}})()"));
                result.Add(new { round, count, samples, sampledRetainedJsBytes = sampled, sampledAllocatedJsBytes = allocated, allocatedByAction, counts = counts.RootElement.Clone() });
            }
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "webview-full-document-performance.json"), JsonSerializer.Serialize(new { fixture = "complete TeX envelope, 120 paragraphs, 0 or 60 comments and image/Bib reference commands", timing = "render event and two animation frames; programmatic selection", allocation = "CDP sampled JavaScript allocations across seven operations, including objects collected by major/minor GC; estimate, not exact total", limitations = "The fixture references an image and bibliography but does not load real assets. The test uses a WebView control, not OS IME or the full project resource pipeline.", viewport = "offscreen 1000x800 WPF host", measurements = result }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Full TeX WebView performance samples saved to " + Path.Combine(output, "webview-full-document-performance.json"));
        }
        finally { host.Close(); }
    }
}
