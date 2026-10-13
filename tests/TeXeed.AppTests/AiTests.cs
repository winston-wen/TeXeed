using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiAsync(MainWindow window, string output)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var editor = (LatexEditor)window.FindName("LeftEditor");
        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var menu = (MenuItem)window.FindName("AiMenuItem");
        Check(!menu.HasItems, "AI menu must have no submenu"); menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); window.UpdateLayout();
        Check(tabs.Owner(TabWorkspace.AiKey) == TabSide.Right && ai.IsVisible, "AI button did not open right AI tab");
        Check(!tabs.Sources.Contains(TabWorkspace.AiKey), "AI tab treated as source file");
        Invoke(window, "MoveWorkspaceTab", TabWorkspace.AiKey, TabSide.Left, 0, false); window.UpdateLayout();
        Check(tabs.Owner(TabWorkspace.AiKey) == TabSide.Left && ai.IsVisible, "AI tab did not move left");
        Invoke(window, "CloseTabs", TabWorkspace.AiKey, TabCloseScope.This); Check(tabs.Owner(TabWorkspace.AiKey) is null, "AI close failed");
        editor.Select(0, 14); var text = editor.SelectedText;
        Invoke(window, "CollectSourceForAi", editor); window.UpdateLayout();
        var book = (AiNotebook)Field(ai, "_book")!;
        Check(book.Entries.Count == 1 && book.Entries[0].Sources.Single().Text == text, "Source snapshot differs from selected text");
        Check(editor.ContextMenu.Items.OfType<MenuItem>().Count() >= 12, "Editor context menu missing edit commands");
        book.Entries[0].Question = "解释这一行"; await ai.SaveAsync();
        var project = (TexProject)Field(window, "_project")!;
        var restored = AiStorage.Load(project.MainPath); Check(restored.Entries.Single().Question == "解释这一行", "Project AI notebook did not persist");
        Check(AiStorage.NotebookPath(project.MainPath) != AiStorage.NotebookPath(project.MainPath + "x"), "Project notebook isolation failed");
        var credentials = new AiCredentials { ApiKey = "test-key-never-send", UseApiKey = true }; credentials.Save();
        Check(AiCredentials.Load().ApiKey == credentials.ApiKey && !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AiStorage.Home, "credentials.dpapi"))).Contains(credentials.ApiKey), "DPAPI secret storage failed");
        var identity = MakeTestIdentity();
        Check(ChatGptLogin.ValidateIdentity(identity.Jwt, "test-client", "test-nonce", identity.Keys).Subject == "test-user", "Valid OIDC identity rejected");
        foreach (var (client, nonce) in new[] { ("bad-client", "test-nonce"), ("test-client", "bad-nonce") })
        { var rejected = false; try { ChatGptLogin.ValidateIdentity(identity.Jwt, client, nonce, identity.Keys); } catch (CryptographicException) { rejected = true; } Check(rejected, "OIDC binding not checked"); }
        var sigRejected = false; try { ChatGptLogin.ValidateIdentity(identity.Jwt[..^10] + "AAAAAAAAAA", "test-client", "test-nonce", identity.Keys); } catch (CryptographicException) { sigRejected = true; } Check(sigRejected, "Invalid signature accepted");
        var files = new[] { new ProjectFile("C:/project/main.tex", "main.tex", ProjectFileKind.Tex, "first\nA compact proof.\nlast") };
        Check(AiResponses.ExecuteTool("search_source", "{\"query\":\"compact proof\"}", files).Contains("main.tex:2"), "Source search lacks location");
        Check(AiResponses.ExecuteTool("read_source", "{\"file\":\"../../secrets\",\"start_line\":1,\"line_count\":20}", files).Contains("不在"), "Tool escaped project snapshot");
        using var handler = new AiFakeHandler(); using var connection = new AiConnection(handler);
        var answer = new StringBuilder();
        await new AiResponses(connection).RunAsync("test-model", restored.Entries, [], files, x => answer.Append(x), _ => { }, CancellationToken.None);
        Check(answer.ToString() == "定位到 main.tex:2。" && handler.Requests == 2, "Streaming tool roundtrip failed");
        Check(handler.SawToolOutput && handler.SawContract, "Responses request broke OAuth-compatible streaming contract");
        using var brokenConnection = new AiConnection(new AiFakeHandler { Broken = true }); var failed = false;
        try { await new AiResponses(brokenConnection).RunAsync("test-model", restored.Entries, [], files, _ => { }, _ => { }, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
        Check(failed, "Interrupted stream treated as success");
        using (var batchPanel = new AiPanel(new AiConnection(new AiFakeHandler())))
        {
            await batchPanel.SetProjectAsync(Path.Combine(output, "batch-fixture.tex")); batchPanel.ProjectFiles = () => files;
            var modelBox = (ComboBox)batchPanel.FindName("Models"); modelBox.ItemsSource = new[] { new AiModel("test-model", "测试模型") }; modelBox.SelectedIndex = 0;
            var first = new AiEntry { Question = "第一问" }; var second = new AiEntry { Question = "第二问" }; var skip = new AiEntry { Question = "不发送", Selected = false };
            batchPanel.AddEntry(first); batchPanel.AddEntry(second); batchPanel.AddEntry(skip); await batchPanel.SubmitAsync();
            Check(first.State == "已完成" && second.State == "已完成" && skip.State == "待提交", "Batch submission did not honor selected entries");
            var batchBook = (AiNotebook)Field(batchPanel, "_book")!;
            Check(batchBook.Turns.Count == 1 && batchBook.Turns[0].Question.Contains("第一问") && batchBook.Turns[0].Question.Contains("第二问") && !batchBook.Turns[0].Question.Contains("不发送"), "Batch history mixed unselected questions");
        }
        using (var failedPanel = new AiPanel(new AiConnection(new AiFakeHandler { Broken = true })))
        {
            await failedPanel.SetProjectAsync(Path.Combine(output, "failure-fixture.tex")); failedPanel.ProjectFiles = () => files;
            var models = (ComboBox)failedPanel.FindName("Models"); models.ItemsSource = new[] { new AiModel("test-model", "测试模型") }; models.SelectedIndex = 0;
            var entry = new AiEntry { Question = "保留待重试" }; failedPanel.AddEntry(entry); await failedPanel.SubmitAsync();
            Check(entry.Selected && entry.State == "未完成，可重试", "Failed batch lost selection or marked successful");
        }
        var switched = false;
        using (var changedConnection = new AiConnection(new AiFakeHandler { SwitchAccount = true }))
        {
            try { await new AiResponses(changedConnection).RunAsync("test-model", restored.Entries, [], files, _ => { }, _ => { }, CancellationToken.None); }
            catch (InvalidOperationException) { switched = true; }
        }
        Check(switched, "Mid-request credential switch was not rejected"); credentials.Save();
        var crop = new PdfPageCrop(20, 400, [new(50, 100), new(300, 350)]);
        var bands = PdfPreview.SelectionBands(new Rect(0, 20, 200, 60), new Size(400, 100), new Size(600, 800), crop);
        Check(bands.Count == 2 && bands[0].Top == 70 && bands[0].Height == 30 && bands[1].Top == 300 && bands[1].Height == 30, "Compressed PDF gap incorrectly included in selection");
        var preview = (PdfPreview)window.FindName("Preview"); Invoke(window, "SelectWorkspaceTab", tabs.Owner(TabWorkspace.PreviewKey)!.Value, TabWorkspace.PreviewKey);
        var pdf = Path.Combine(output, "ai-preview.pdf"); WriteThemePdf(pdf); preview.SetCropOptions(false, false); await preview.LoadAsync(pdf); window.UpdateLayout();
        preview.BeginAiSelection();
        var layouts = ((System.Collections.IEnumerable)Field(preview, "_layouts")!).Cast<object>().ToArray();
        var top0 = (double)layouts[0].GetType().GetProperty("Top")!.GetValue(layouts[0])!; var top1 = (double)layouts[1].GetType().GetProperty("Top")!.GetValue(layouts[1])!;
        var left0 = (double)layouts[0].GetType().GetProperty("Left")!.GetValue(layouts[0])!;
        var selection = new Rect(left0 + 30, top0 + 50, 150, top1 - top0 + 30);
        typeof(PdfPreview).GetField("_aiSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(preview, selection);
        await preview.ConfirmAiSelectionAsync(); window.UpdateLayout();
        var pdfEntry = book.Entries.Last();
        Check(pdfEntry.PdfBlocks.Select(b => b.Page).Distinct().SequenceEqual(new[] { 1, 2 }) && pdfEntry.PdfRevision.Length == 64, "Cross-page PDF capture did not preserve pages/revision");
        Check(pdfEntry.PdfBlocks.All(b => b.Width > 0 && b.Height > 0 && Convert.FromBase64String(b.ImageBase64).Take(4).SequenceEqual(new byte[] { 137, 80, 78, 71 })), "PDF screenshots invalid");
        Check(tabs.FocusedTab == TabWorkspace.AiKey, "PDF selection did not open AI");
        Capture(window, Path.Combine(output, "ai-tab.png"));
        await CallAsync(window, "HandleShortcutAsync", CreateKey(window, Key.F4, Keyboard.PreviewKeyDownEvent), ModifierKeys.Control);
        Check(tabs.Owner(TabWorkspace.AiKey) is null && tabs.Owner(TabWorkspace.PreviewKey) is not null, "Ctrl+F4 AI closing affected protected preview");
        Invoke(window, "OpenTitleSubmenu", menu); Check(tabs.Owner(TabWorkspace.AiKey) is not null, "Alt+A failed to activate AI leaf menu");
        await ai.SaveAsync();
        Console.WriteLine($"AI: {count} checks passed (OAuth identity, encrypted credentials, streaming/tool calls, source/PDF context, tabs, persistence).");
    }
    private static (string Jwt, JsonElement Keys) MakeTestIdentity()
    {
        using var rsa = RSA.Create(2048); var key = rsa.ExportParameters(false); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var h = ChatGptLogin.Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"kid\":\"test\"}"));
        var p = ChatGptLogin.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = ChatGptLogin.Issuer, aud = "test-client", sub = "test-user", email = "test@example.com", iat = now, exp = now + 300, nonce = "test-nonce" }));
        var signature = ChatGptLogin.Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(h + "." + p), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var keys = JsonSerializer.SerializeToElement(new { keys = new[] { new { kid = "test", kty = "RSA", n = ChatGptLogin.Base64Url(key.Modulus!), e = ChatGptLogin.Base64Url(key.Exponent!) } } });
        return (h + "." + p + "." + signature, keys);
    }
    private sealed class AiFakeHandler : HttpMessageHandler
    {
        public int Requests; public bool Broken, SawToolOutput, SawContract, SwitchAccount;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath != "/v1/responses") throw new Exception("Unexpected network endpoint in AI test");
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            SawContract = json["store"]!.GetValue<bool>() == false && json["stream"]!.GetValue<bool>() && json["tools"]![0]!["type"]!.GetValue<string>() == "namespace";
            if (Requests > 1) SawToolOutput = json["input"]!.AsArray().Any(n => n?["type"]?.GetValue<string>() == "function_call_output" && n["output"]!.GetValue<string>().Contains("main.tex:2"));
            var data = Broken ? "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n" : Requests == 1
                ? "data: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"function_call\",\"name\":\"search_source\",\"call_id\":\"c1\",\"arguments\":\"{\\\"query\\\":\\\"compact proof\\\"}\"}]}}\n\n"
                : "data: {\"type\":\"response.output_text.delta\",\"delta\":\"定位到 main.tex:2。\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            if (SwitchAccount && Requests == 1) { var changed = AiCredentials.Load(); changed.ApiKey = "different-test-key"; changed.Save(); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        }
    }
}
