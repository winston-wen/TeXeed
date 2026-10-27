using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var outputArgument = Array.IndexOf(args, "--output");
        var output = outputArgument >= 0 && outputArgument + 1 < args.Length
            ? Path.GetFullPath(args[outputArgument + 1]) : Path.Combine(root, "artifacts", "ui-check");
        Directory.CreateDirectory(output);
        SettingsMigrationTests.Run(Path.Combine(output, "settings-migration"));
        var config = Path.Combine(output, "profile"); Directory.CreateDirectory(config);
        Environment.SetEnvironmentVariable("TEXEED_HOME", config);
        var expectedAiHome = Path.GetFullPath(Path.Combine(config, "ai"));
        if (!string.Equals(Path.GetFullPath(TeXeed.App.AI.AiStorage.Home), expectedAiHome, StringComparison.OrdinalIgnoreCase)
            || expectedAiHome.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeXeed") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UI tests require an isolated AI credentials directory.");
        Console.WriteLine("Isolated test AI home: " + expectedAiHome);
        File.WriteAllText(Path.Combine(config, "settings.json"), JsonSerializer.Serialize(new AppSettings { LivePreview = false }));
        var result = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TeXeed;component/Theme.xaml", UriKind.Relative) });
        var window = new MainWindow { Width = 1440, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30 };
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); e.Handled = true; app.Shutdown(1); };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(200);
                var mainMenu = (Menu)window.FindName("MainMenu");
                if (mainMenu.Visibility != Visibility.Visible) throw new Exception("Wide title bar must show its menu");
                var windowGrid = (Grid)window.Content;
                if (windowGrid.RowDefinitions.Any(row => row.Height.IsAbsolute && Math.Abs(row.Height.Value - 64) < 0.1))
                    throw new Exception("The removed top toolbar still reserves a 64 px row");
                if (Math.Abs(((FrameworkElement)window.FindName("Welcome")).TranslatePoint(new Point(), windowGrid).Y - 40) > 1)
                    throw new Exception("Welcome page must begin directly below the custom title bar");
                Capture(window, Path.Combine(output, "welcome.png"));
                if (args.Contains("--ai-reading-media-only"))
                {
                    await CheckAiReadingMediaAsync(window, output, args.Contains("--ai-reading-media-input-review"));
                    await CloseAndObserveAsync(window, "AI reading/media regression"); result = 0; return;
                }
                if (args.Contains("--welcome-only"))
                {
                    await CheckWelcomeAsync(window, output);
                    await CloseAndObserveAsync(window, "Welcome page regression"); result = 0; return;
                }
                if (args.Contains("--code-style-only"))
                {
                    await CheckMarkdownCodeAsync(window, output);
                    await CloseAndObserveAsync(window, "Markdown code style regression"); result = 0; return;
                }
                var fixture = Path.Combine(output, "project"); Directory.CreateDirectory(Path.Combine(fixture, "sections"));
                var main = Path.Combine(fixture, "main.tex");
                File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\n\\section{Introduction}\n\\label{sec:intro}\nHello from TeXeed.\n\\input{sections/method}\n\\end{document}\n");
                var child = Path.Combine(fixture, "sections", "method.tex");
                File.WriteAllText(child, "\\section{Method}\n\\label{sec:method}\n% TODO: Check equation\nA compact proof.\n");
                await CallAsync(window, "OpenAsync", main);
                var editor = (LatexEditor)window.FindName("Editor");
                if (editor.CurrentFilePath != main) throw new Exception("Main editor did not open fixture");
                var project = (TexProject)Field(window, "_project")!;
                if (project.Files.Count != 2) throw new Exception("Dependent file was not loaded");
                var existingFollow = Array.IndexOf(args, "--follow-existing");
                if (args.Contains("--print-review"))
                {
                    var printPath = Path.Combine(output, "print-source.pdf"); WritePrintPdf(printPath);
                    var printWindow = new PdfPrintWindow(printPath, "打印界面验证", viewRegion: new Rect(80, 120, 300, 400)) { Owner = window };
                    printWindow.ShowDialog();
                    await CloseAndObserveAsync(window, "PDF print manual review"); result = 0; return;
                }
                if (args.Contains("--print-only"))
                {
                    await CheckPdfPrintingAsync(window, output);
                    await CloseAndObserveAsync(window, "PDF printing regression"); result = 0; return;
                }
                if (args.Contains("--ai-permissions-only"))
                {
                    await CheckAiPermissionsAsync(window, output);
                    await CloseAndObserveAsync(window, "AI permissions and shell regression"); result = 0; return;
                }
                if (args.Contains("--ai-focus-only"))
                {
                    await CheckAiFocusAsync(window);
                    result = 0; app.Shutdown(); return;
                }
                if (args.Contains("--literal-text-only"))
                {
                    await CheckLiteralTextAsync(window, output);
                    await CloseAndObserveAsync(window, "Literal text regression"); result = 0; return;
                }
                if (args.Contains("--ai-history-only"))
                {
                    await CheckAiHistoryAsync(window, output);
                    await CloseAndObserveAsync(window, "AI history management regression"); result = 0; return;
                }
                if (args.Contains("--ai-reliability-only"))
                {
                    await CheckAiReliabilityAsync(window, output);
                    await CloseAndObserveAsync(window, "AI reliability regression"); result = 0; return;
                }
                if (args.Contains("--ai-prototype-only"))
                {
                    await CheckAiPrototypeAsync(window, output);
                    await CloseAndObserveAsync(window, "AI prototype parity regression"); result = 0; return;
                }
                if (args.Contains("--ai-header-only"))
                {
                    await CheckAiHeaderAsync(window, output);
                    await CloseAndObserveAsync(window, "AI header hover regression"); result = 0; return;
                }
                if (args.Contains("--ai-reading-only"))
                {
                    await CheckAiReadingAsync(window, output);
                    await CloseAndObserveAsync(window, "AI reading regression"); result = 0; return;
                }
                if (args.Contains("--ai-polish-only"))
                {
                    await CheckAiPolishAsync(window, output);
                    await CloseAndObserveAsync(window, "AI polish regression"); result = 0; return;
                }
                if (args.Contains("--ai-presentation-only"))
                {
                    await CheckAiPresentationAsync(window, output);
                    await CloseAndObserveAsync(window, "AI presentation"); result = 0; return;
                }
                if (args.Contains("--ai-ui-perf-only"))
                {
                    var compare = Array.IndexOf(args, "--baseline");
                    await CheckAiUiPerformanceAsync(window, output, compare >= 0 ? args[compare + 1] : null, args.Contains("--normalize-typography"));
                    await CloseAndObserveAsync(window, "AI UI performance"); result = 0; return;
                }
                if (args.Contains("--ai-compact-only"))
                {
                    await CheckAiCompactAsync(window, output);
                    await CloseAndObserveAsync(window, "AI compact composer and activity regression"); result = 0; return;
                }
                if (args.Contains("--ai-dependency-only"))
                {
                    await CheckAiCompileDependenciesAsync(window, root, output);
                    await CloseAndObserveAsync(window, "AI compile dependency regression"); result = 0; return;
                }
                if (args.Contains("--ai-entry-only"))
                {
                    await CheckAiCompileEntriesAsync(window, root, output);
                    await CloseAndObserveAsync(window, "AI compile entry regression"); result = 0; return;
                }
                if (args.Contains("--ai-discovery-only"))
                {
                    await CheckAiDiscoveryAsync(window, root, output);
                    await CloseAndObserveAsync(window, "AI project discovery regression"); result = 0; return;
                }
                if (args.Contains("--ai-agent-only"))
                {
                    await CheckAiAgentAsync(window, root, output);
                    await CloseAndObserveAsync(window, "AI editing/compilation regression"); result = 0; return;
                }
                if (args.Contains("--ai-selection-only"))
                {
                    foreach (var theme in new[] { "ristretto", "github-light-default" })
                    { TeXeed.App.Services.ThemeManager.Apply(theme); await CheckSelectionPixelsAsync(window, output, theme); }
                    await CloseAndObserveAsync(window, "Selection pixel regression"); result = 0; return;
                }
                if (args.Contains("--ai-math-scroll-only"))
                {
                    await CheckAiMathScrollAsync(window, output);
                    await CloseAndObserveAsync(window, "AI math and mouse wheel regression"); result = 0; return;
                }
                if (args.Contains("--ai-math-perf-only"))
                {
                    await CheckAiMathPerformanceAsync(output);
                    await CloseAndObserveAsync(window, "AI math performance"); result = 0; return;
                }
                if (args.Contains("--ai-math-batch-only"))
                {
                    await CheckAiMathBatchAsync(output);
                    await CloseAndObserveAsync(window, "AI math batch recovery"); result = 0; return;
                }
                if (args.Contains("--ai-math-copy-only"))
                {
                    await CheckAiMathCopyAsync(window, output);
                    await CloseAndObserveAsync(window, "AI math copy"); result = 0; return;
                }
                if (args.Contains("--ai-math-proofread-only"))
                {
                    await CheckAiMathProofreadingAsync(window, output);
                    await CloseAndObserveAsync(window, "AI math proofreading"); result = 0; return;
                }
                if (args.Contains("--ai-annotations-only"))
                {
                    await CheckAiAnnotationsAsync(window, output);
                    await CloseAndObserveAsync(window, "AI annotation and block rendering regression"); result = 0; return;
                }
                if (args.Contains("--ai-pins-only"))
                {
                    await CheckAiPinsAsync(window, output);
                    await CloseAndObserveAsync(window, "AI pin editor regression"); result = 0; return;
                }
                if (args.Contains("--ai-only"))
                {
                    await CheckAiAsync(window, output);
                    await CheckAiRenderingAsync(window, output);
                    await CloseAndObserveAsync(window, "AI workspace regression"); result = 0; return;
                }
                if (args.Contains("--preview-settings-only"))
                {
                    await CheckPreviewSettingsAsync(window, output);
                    await CloseAndObserveAsync(window, "live preview settings regression");
                    result = 0; return;
                }
                if (args.Contains("--dock-only"))
                {
                    await CheckDockingAsync(window, output);
                    await CloseAndObserveAsync(window, "docking workspace regression");
                    result = 0; return;
                }
                if (args.Contains("--bookmarks-only"))
                {
                    await CheckBookmarksAsync(window, output);
                    await CloseAndObserveAsync(window, "bookmark sidebar regression");
                    result = 0; return;
                }
                var sidebarPerformance = Array.IndexOf(args, "--sidebar-performance");
                if (sidebarPerformance >= 0)
                {
                    await CheckSidebarPerformanceAsync(window, Path.GetFullPath(args[sidebarPerformance + 1]), output);
                    await CloseAndObserveAsync(window, "sidebar performance regression");
                    result = 0; return;
                }
                if (args.Contains("--grab-only"))
                {
                    await CheckGrabGesturesAsync(window, output);
                    await CloseAndObserveAsync(window, "grab cursor regression");
                    result = 0; return;
                }
                if (args.Contains("--sidebar-trees-only"))
                {
                    await CheckSidebarTreesAsync(window, output);
                    await CloseAndObserveAsync(window, "sidebar trees regression");
                    result = 0; return;
                }
                if (args.Contains("--file-explorer-only"))
                {
                    await CheckFileExplorerAsync(window, output);
                    await CloseAndObserveAsync(window, "file explorer regression"); result = 0; return;
                }
                if (args.Contains("--file-priority-only"))
                {
                    await CheckFilePriorityAsync(window, output);
                    await CloseAndObserveAsync(window, "file priority, rename, middle click and performance regression"); result = 0; return;
                }
                if (args.Contains("--tabs-only"))
                {
                    await CheckEditorTabsAsync(window, output);
                    await CloseAndObserveAsync(window, "editor tabs regression");
                    result = 0; return;
                }
                var startupSync = Array.IndexOf(args, "--startup-sync");
                if (startupSync >= 0)
                {
                    await CheckStartupSyncAsync(window, Path.GetFullPath(args[startupSync + 1]), output);
                    await CloseAndObserveAsync(window, "startup sync regression");
                    result = 0; return;
                }
                var cachedBook = Array.IndexOf(args, "--cache-book");
                if (cachedBook >= 0)
                {
                    await CheckCachedBookAsync(window, Path.GetFullPath(args[cachedBook + 1]), output);
                    await CloseAndObserveAsync(window, "book preview cache regression");
                    result = 0; return;
                }
                if (args.Contains("--cache-only"))
                {
                    await CheckPreviewCacheAsync(window, output);
                    await CloseAndObserveAsync(window, "preview cache regression");
                    result = 0; return;
                }
                var chapterFollow = Array.IndexOf(args, "--follow-chapters");
                if (chapterFollow >= 0)
                {
                    await CheckChapterFollowAsync(window, Path.GetFullPath(args[chapterFollow + 1]), output);
                    await CloseAndObserveAsync(window, "chapter start follow regression");
                    result = 0; return;
                }
                if (args.Contains("--caption-only"))
                {
                    await CheckTitleBarAsync(window, output, includeFullscreen: false);
                    await CloseAndObserveAsync(window, "Caption regression"); result = 0; return;
                }
                if (args.Contains("--titlebar-only"))
                {
                    await CheckTitleBarAsync(window, output);
                    Invoke(window, "ToggleFullscreen");
                    Invoke(window, "ProcessMenuKeyDown", Key.LeftAlt, ModifierKeys.Alt, false);
                    Invoke(window, "ProcessMenuKeyUp", Key.LeftAlt, ModifierKeys.None);
                    if (!((FrameworkElement)window.FindName("TitleBar")).IsVisible) throw new Exception("Fullscreen titlebar was unavailable for closing");
                    ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => !window.IsVisible, "Caption close button did not close the window");
                    result = 0; return;
                }
                if (existingFollow >= 0)
                {
                    await CheckExistingFollowAsync(window, Path.GetFullPath(args[existingFollow + 1]), output);
                    await CloseAndObserveAsync(window, "existing follow regression");
                    result = 0; return;
                }
                if (args.Contains("--layout-only"))
                {
                    await CheckLayoutMetadataAsync(window, root, output);
                    await CloseAndObserveAsync(window, "page layout regression");
                    result = 0; return;
                }
                if (args.Contains("--completion-only"))
                {
                    await CheckCompletionKeysAsync();
                    await CloseAndObserveAsync(window, "completion regression");
                    result = 0; return;
                }
                if (args.Contains("--themes-only"))
                {
                    await CheckAppearanceAsync(window, output);
                    await CheckThemesAsync(window, output);
                    await CloseAndObserveAsync(window, "theme regression");
                    result = 0; return;
                }
                if (args.Contains("--workspace-only"))
                {
                    await CheckNavigationAsync(window, output);
                    await CloseAndObserveAsync(window, "workspace icons regression");
                    result = 0; return;
                }
                if (args.Contains("--word-wrap-only"))
                {
                    await CheckWordWrapAsync(window, output);
                    await CloseAndObserveAsync(window, "word wrap regression");
                    result = 0; return;
                }
                if (args.Contains("--crop-only"))
                {
                    await CheckPdfCropAsync(window, root, output);
                    await CloseAndObserveAsync(window, "crop regression");
                    result = 0; return;
                }
                if (args.Contains("--appearance-only"))
                {
                    await CheckAppearanceAsync(window, output);
                    await CheckFindPanelAsync(window, output);
                    await CheckReplacePanelAsync(output);
                    await CloseAndObserveAsync(window, "appearance regression");
                    result = 0; return;
                }
                if (args.Contains("--outline-interactive"))
                {
                    await InspectOutlineClicksAsync(window, output);
                    result = 0; return;
                }
                var profileIndex = Array.IndexOf(args, "--outline-profile");
                if (profileIndex >= 0)
                {
                    await ProfileOutlineAsync(window, args[profileIndex + 1], args.ElementAtOrDefault(profileIndex + 2));
                    await CloseAndObserveAsync(window, "outline profile");
                    result = 0; return;
                }
                if (args.Contains("--outline-only", StringComparer.Ordinal))
                {
                    await CheckOutlineAsync(window, output);
                    await CheckOutlineDecorationsAsync(window, output);
                    await CheckNavigationAsync(window, output);
                    await CloseAndObserveAsync(window, "outline regression");
                    result = 0; return;
                }
                if (args.Contains("--outline-preview-only", StringComparer.Ordinal))
                {
                    await CheckOutlinePreviewAsync(window, output);
                    await CloseAndObserveAsync(window, "outline preview-only navigation regression");
                    result = 0; return;
                }
                if (args.Contains("--close-only", StringComparer.Ordinal))
                {
                    await CheckClosingAsync(window, main);
                    if (app.Windows.OfType<MainWindow>().Any()) throw new Exception("A MainWindow remained open after the close regressions");
                    result = 0; return;
                }
                if (args.Contains("--find-only", StringComparer.Ordinal))
                {
                    await CheckFindPanelAsync(window, output);
                    await CheckReplacePanelAsync(output);
                    await CloseAndObserveAsync(window, "find-panel regression");
                    result = 0; return;
                }
                if (args.Contains("--viewport-only", StringComparer.Ordinal))
                {
                    await CheckViewportFollowingAsync(window, root, output);
                    await CloseAndObserveAsync(window, "viewport-following regression");
                    result = 0; return;
                }
                if (args.Contains("--build-scheduling-only", StringComparer.Ordinal))
                {
                    await CheckBuildSchedulingAsync(window, root, output);
                    await CloseAndObserveAsync(window, "build-scheduling regression");
                    result = 0; return;
                }
                Invoke(window, "ActivateFile", child);
                editor.Document.Insert(editor.Document.TextLength, "A new paragraph.\n");
                Invoke(window, "ActivateFile", main);
                editor.Document.Insert(editor.Document.TextLength, "% Unsaved main edit\n");
                await CallAsync(window, "SaveAsync");
                if (!File.ReadAllText(child).Contains("A new paragraph.")) throw new Exception("Child edit was not saved");
                if (!File.ReadAllText(main).Contains("Unsaved main edit")) throw new Exception("Main edit was not saved");
                var beforeSave = editor.Text;
                editor.Document.Insert(editor.Document.TextLength, "% edit during save\n");
                var saving = (Task)Invoke(window, "SaveAsync")!;
                editor.Text = beforeSave;
                await saving;
                var dirty = (Dictionary<string, string>)Field(window, "_changes")!;
                if (!dirty.TryGetValue(main, out var reverted) || reverted != beforeSave) throw new Exception("Undo during save was lost");
                await CallAsync(window, "SaveAsync");
                if (File.ReadAllText(main) != beforeSave) throw new Exception("Undo during save was not persisted");
                // Detaching an open file keeps its buffer writable until project close.
                editor.Document.Replace(editor.Text.IndexOf("\\input", StringComparison.Ordinal), "\\input{sections/method}".Length, "% include removed");
                await CallAsync(window, "SaveAsync");
                await Task.Delay(750);
                Invoke(window, "ActivateFile", child);
                if (editor.CurrentFilePath != child) throw new Exception("Detached open file was lost");
                editor.Document.Insert(editor.Document.TextLength, "% detached edit\n");
                await CallAsync(window, "SaveAsync");
                if (!File.ReadAllText(child).Contains("detached edit")) throw new Exception("Detached buffer could not save");
                Invoke(window, "ActivateFile", main);
                var engine = Path.Combine(root, ".tools", "tectonic", "tectonic.exe");
                if (File.Exists(engine))
                {
                    Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
                    window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new AppSettings { LivePreview = false, Engine = TexEngine.Tectonic, EnginePath = engine });
                    editor.Document.Insert(editor.Document.TextLength, "% Ctrl+S must compile with live preview disabled\n");
                    var saveKey = CreateKey(window, Key.S, Keyboard.PreviewKeyDownEvent);
                    await CallAsync(window, "HandleShortcutAsync", saveKey, ModifierKeys.Control);
                    if (!saveKey.Handled || !File.ReadAllText(main).Contains("Ctrl+S must compile"))
                        throw new Exception("Ctrl+S did not handle and save the source edit");
                    var generated = (string?)Field(window, "_pdfPath");
                    if (generated == null || !File.Exists(generated)) throw new Exception("WPF build did not generate a PDF: " + ((TextBox)window.FindName("LogText")).Text);
                    if ((int)Field(window, "_pdfRevision")! != (int)Field(window, "_revision")! || ((AppSettings)Field(window, "_settings")!).LivePreview)
                        throw new Exception("Ctrl+S did not compile the saved revision with live preview disabled");
                    Console.WriteLine("PASS: Ctrl+S saves and compiles with live preview disabled");
                    editor.NavigateTo(new SourceLocation(main, 5));
                    await CallAsync(window, "ForwardAsync", true);
                    var loadedProject = (TexProject)Field(window, "_project")!;
                    var point = await new SyncTexService().ForwardAsync(loadedProject, generated, new SourceLocation(main, 5), new AppSettings());
                    if (point == null) throw new Exception("UI build did not produce a SyncTeX mapping");
                    await CallAsync(window, "InverseAsync", point);
                    if (editor.CurrentFilePath != main) throw new Exception("PDF inverse navigation did not select source");
                    var settingsField = window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    settingsField.SetValue(window, ((AppSettings)settingsField.GetValue(window)!) with { LivePreview = true });
                    editor.Document.Insert(editor.Document.TextLength, "% live preview test\n");
                    var expectedRevision = (int)Field(window, "_revision")!;
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while ((int)Field(window, "_pdfRevision")! != expectedRevision && DateTime.UtcNow < deadline) await Task.Delay(150);
                    if ((int)Field(window, "_pdfRevision")! != expectedRevision || !File.ReadAllText(main).Contains("live preview test")) throw new Exception("Live preview did not save and refresh PDF");
                    settingsField.SetValue(window, ((AppSettings)settingsField.GetValue(window)!) with { LivePreview = false });
                    Console.WriteLine("PASS: actual WPF compile, PDF load, forward/inverse SyncTeX through MainWindow");
                    Console.WriteLine("PASS: editing triggers automatic save, compile and PDF refresh");
                }
                if (File.Exists(engine))
                {
                    await CheckBuildSchedulingAsync(window, root, output);
                    await CheckViewportFollowingAsync(window, root, output);
                }
                // Render a real PDF if integration test has already produced one.
                var pdf = Path.Combine(root, "examples", "ResearchNote", ".texeed", "preview", "main.pdf");
                if (File.Exists(pdf) && File.Exists(engine))
                {
                    await CallAsync(window, "OpenAsync", Path.Combine(root, "examples", "ResearchNote", "main.tex"));
                    await CallAsync(window, "BuildAsync");
                    var research = (TexProject)Field(window, "_project")!;
                    var intro = research.Files.First(f => f.Name == "introduction.tex");
                    Invoke(window, "ActivateFile", intro.Path);
                }
                await Task.Delay(600); window.UpdateLayout();
                await WaitForPreviewRenderedAsync(window);
                Capture(window, Path.Combine(output, "workspace.png"));
                await CheckFindPanelAsync(window, output);
                await CheckReplacePanelAsync(output);
                await CheckCompletionKeysAsync();
                await CheckNavigationAsync(window, output);
                await CheckOutlineAsync(window, output);
                await CheckOutlineDecorationsAsync(window, output);
                await CheckAppearanceAsync(window, output);
                await CheckThemesAsync(window, output);
                await CheckPdfCropAsync(window, root, output);
                ((Expander)window.FindName("LogExpander")).IsExpanded = true;
                window.Width = 1050; window.Height = 640;
                await WaitForPreviewRenderedAsync(window);
                Capture(window, Path.Combine(output, "compact.png"));
                var settings = new SettingsWindow(new AppSettings()) { Owner = window }; settings.Show();
                await Task.Delay(200); Capture(settings, Path.Combine(output, "settings.png")); settings.Close();
                Console.WriteLine("PASS: WPF startup, main/child editing, multi-file save, detached buffer preservation, settings and layout rendering");
                await CheckClosingAsync(window, main);
                if (app.Windows.OfType<MainWindow>().Any()) throw new Exception("A MainWindow remained open after the close regressions");
                Console.WriteLine($"Render output: {output}"); result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                // A failed assertion must not leave a dirty-window confirmation blocking the test process.
                // Successful runs exercise the production Close/Closing/Closed path for every test window.
                if (result != 0)
                    foreach (var remaining in app.Windows.OfType<MainWindow>().ToArray())
                    {
                        remaining.GetType().GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(remaining, true);
                        ((CompileService)Field(remaining, "_compiler")!).Cancel(); remaining.Close();
                    }
                app.Shutdown(result);
            }
        };
        window.Show(); app.Run(); return result;
    }
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static object? Invoke(object owner, string method, params object[] args)
    {
        var member = owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var parameters = member.GetParameters();
        return member.Invoke(owner, parameters.Select((p, index) => index < args.Length ? args[index] : p.DefaultValue).ToArray());
    }
    private static async Task CallAsync(object owner, string method, params object[] args) => await (Task)Invoke(owner, method, args)!;
    private static async Task CheckClosingAsync(MainWindow projectWindow, string mainPath)
    {
        if (Field(projectWindow, "_project") is not TexProject || ((Dictionary<string, string>)Field(projectWindow, "_changes")!).Count != 0)
            throw new Exception("Clean-project close regression requires an open, saved project");
        await CloseAndObserveAsync(projectWindow, "clean project");

        var welcome = await ShowCloseTestWindowAsync();
        if (Field(welcome, "_project") != null || ((FrameworkElement)welcome.FindName("Welcome")).Visibility != Visibility.Visible)
            throw new Exception("Welcome close regression unexpectedly opened a project");
        await CloseAndObserveAsync(welcome, "welcome window");

        var compiling = await ShowCloseTestWindowAsync();
        await CallAsync(compiling, "OpenAsync", mainPath);
        var pending = new TaskCompletionSource<CompileResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        compiling.GetType().GetField("_compileTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(compiling, pending.Task);
        compiling.GetType().GetField("_building", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(compiling, true);
        await CloseAndObserveAsync(compiling, "pending compilation", async closed =>
        {
            // Closing resumes at Background priority. ContextIdle runs after that continuation
            // has reached its await, so this barrier proves the window waits for the compile task.
            await compiling.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (closed.IsCompleted || pending.Task.IsCompleted || !(bool)Field(compiling, "_closing")! || !compiling.IsVisible)
                throw new Exception("Window closed before the outstanding compilation completed");
            if (((LatexEditor)compiling.FindName("Editor")).IsEnabled)
                throw new Exception("Editor remained enabled while window close waited for compilation");
            pending.SetResult(new CompileResult(false, null, "Canceled by close regression", [], TimeSpan.Zero, "test", false));
        });
        Console.WriteLine("PASS: actual clean-project and welcome closes; pending compilation completes before Closed; no close reentrancy");
    }

    private static async Task<MainWindow> ShowCloseTestWindowAsync()
    {
        var window = new MainWindow { Width = 1050, Height = 640, ShowInTaskbar = false };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler onLoaded = (_, _) => loaded.TrySetResult();
        window.Loaded += onLoaded;
        try
        {
            window.Show();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return window;
        }
        finally { window.Loaded -= onLoaded; }
    }

    private static async Task CloseAndObserveAsync(MainWindow window, string description, Func<Task, Task>? beforeCompletion = null)
    {
        if ((bool)Field(window, "_allowClose")!) throw new Exception($"{description}: close guard was bypassed before the test");
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCloseReturned = false;
        EventHandler onClosed = (_, _) =>
        {
            if (!firstCloseReturned) closed.TrySetException(new Exception($"{description}: Closed reentered the first Close call"));
            else closed.TrySetResult();
        };
        window.Closed += onClosed;
        try
        {
            window.Close();
            firstCloseReturned = true;
            if (beforeCompletion != null) await beforeCompletion(closed.Task);
            // The timeout is only a failure bound; success is signaled by the actual WPF Closed event.
            try { await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException ex)
            {
                throw new Exception($"{description}: close timed out; closing={Field(window, "_closing")}, transitioning={Field(window, "_transitioning")}, status={((TextBlock)window.FindName("StatusText")).Text}", ex);
            }
            if (window.IsVisible) throw new Exception($"{description}: Closed fired but the window is still visible");
        }
        finally { window.Closed -= onClosed; }
    }

    private static KeyEventArgs CreateKey(Window window, Key key, RoutedEvent routedEvent) => new(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Window is not connected to a presentation source"),
        Environment.TickCount, key) { RoutedEvent = routedEvent };

    private static async Task CheckFindPanelAsync(MainWindow window, string output)
    {
        var editor = (LatexEditor)window.FindName("Editor");
        var search = (Control)Field(editor, "_search")!;
        var workspace = (Grid)window.FindName("Workspace");
        var editorColumn = workspace.ColumnDefinitions[2];
        var oldWidth = window.Width; var oldHeight = window.Height; var oldColumn = editorColumn.Width;
        var oldFont = editor.FontSize; var oldTransform = editor.LayoutTransform; var oldText = editor.Text;
        var oldSelection = editor.SelectionStart; var oldSelectionLength = editor.SelectionLength;
        try
        {
            var replaceShortcut = CreateKey(window, Key.H, Keyboard.PreviewKeyDownEvent);
            await CallAsync(window, "HandleShortcutAsync", replaceShortcut, ModifierKeys.Control);
            if (!replaceShortcut.Handled || search.Tag is not FrameworkElement { Visibility: Visibility.Visible })
                throw new Exception("Ctrl+H did not expand replacement in the editor search panel");
            editor.Select(0, 0); editor.ExecuteCommand("find");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var input = (TextBox)search.Template.FindName("PART_searchTextBox", search);
            input.Text = "section";
            void SetEditorDisplayWidth(double width)
            {
                // Keep sibling panes visible while constraining the editor's display width.
                var siblings = workspace.ColumnDefinitions.Where(column => column != editorColumn)
                    .Sum(column => Math.Max(column.MinWidth, column.ActualWidth));
                var chrome = Math.Max(0, window.ActualWidth - workspace.ActualWidth);
                editorColumn.Width = new GridLength(width);
                window.Width = Math.Max(window.MinWidth, width + siblings + chrome);
            }
            async Task CheckAndCapture(string name, double? expectedEditorWidth = null)
            {
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                if (!search.IsVisible || input.ActualHeight <= 0) throw new Exception($"{name}: editor find panel did not open (visible={search.IsVisible}; visibility={search.Visibility}; loaded={search.IsLoaded}; inputHeight={input.ActualHeight}; closed={search.GetType().GetProperty("IsClosed")!.GetValue(search)}; editorVisible={editor.IsVisible})");
                var root = (FrameworkElement)window.Content;
                if (expectedEditorWidth is { } width)
                {
                    var bounds = editor.TransformToAncestor(root).TransformBounds(new Rect(new Point(), editor.RenderSize));
                    var visible = VisibleBounds(editor, root);
                    if (Math.Abs(bounds.Width - width) > 1 || visible.IsEmpty || visible.Width < bounds.Width - 1 || visible.Height < bounds.Height - 1)
                        throw new Exception($"{name}: fixture requires a visible {width}-DIP editor (bounds {bounds}, visible {visible})");
                }
                var textView = Descendants<FrameworkElement>(input).FirstOrDefault(view => view.GetType().Name == "TextBoxView") ?? input;
                var textViewport = VisibleBounds(textView, root);
                foreach (var index in new[] { 0, input.Text.Length - 1 })
                {
                    var character = input.GetRectFromCharacterIndex(index);
                    if (character.IsEmpty || character.Height < 8) throw new Exception($"{name}: find text has no usable line layout");
                    character.Width = Math.Max(1, character.Width);
                    var glyph = input.TransformToAncestor(root).TransformBounds(character);
                    var visibleGlyph = Rect.Intersect(glyph, textViewport);
                    if (visibleGlyph.IsEmpty || visibleGlyph.Height < glyph.Height - 1 || visibleGlyph.Width < glyph.Width - 1)
                        throw new Exception($"{name}: find text is clipped (line {glyph}, visible {visibleGlyph})");
                }
                var buttons = Descendants<Button>(search).Where(button => button.Name is not
                    ("PART_replaceToggle" or "ReplaceCurrentButton" or "ReplaceAllButton")).ToArray();
                if (buttons.Length < 3) throw new Exception($"{name}: previous/next/close search buttons are missing");
                foreach (var button in buttons)
                {
                    var icons = Descendants<Image>(button).Where(image => image.Source != null).Cast<FrameworkElement>()
                        .Concat(Descendants<System.Windows.Shapes.Path>(button).Where(path => path.Data != null && (path.Fill != null || path.Stroke != null))).ToArray();
                    if (icons.Length == 0) throw new Exception($"{name}: a search button has no rendered icon");
                    foreach (var icon in icons)
                    {
                        var bounds = icon.TransformToAncestor(root).TransformBounds(new Rect(new Point(), icon.RenderSize));
                        var visible = Rect.Intersect(VisibleBounds(icon, root), VisibleBounds(button, root));
                        if (bounds.Width < 4 || bounds.Height < 4 || visible.IsEmpty || visible.Width < bounds.Width - 1 || visible.Height < bounds.Height - 1)
                            throw new Exception($"{name}: search icon is empty or clipped (icon {bounds}, visible {visible})");
                    }
                }
                await WaitForPreviewRenderedAsync(window);
                Capture(window, Path.Combine(output, name + ".png"));
            }
            await CheckAndCapture("find-normal");
            window.Height = window.MinHeight; editor.FontSize = 24;
            SetEditorDisplayWidth(300);
            await CheckAndCapture("find-narrow-large-font", 300);
            // 150% scaling keeps the minimum 300-DIP editor area at 450 display units.
            editor.LayoutTransform = new ScaleTransform(1.5, 1.5);
            SetEditorDisplayWidth(450);
            await CheckAndCapture("find-scaled", 450);
            if (editor.Text != oldText) throw new Exception("Find-panel checks modified the document");
            Console.WriteLine("PASS: themed find input and previous/next/close icons remain visible at normal, narrow/large-font and 150% scales");
        }
        finally
        {
            search.GetType().GetMethod("Close", Type.EmptyTypes)!.Invoke(search, null);
            editor.LayoutTransform = oldTransform; editor.FontSize = oldFont; editorColumn.Width = oldColumn;
            window.Width = oldWidth; window.Height = oldHeight; editor.Select(oldSelection, oldSelectionLength); window.UpdateLayout();
        }
    }

    private static async Task CheckReplacePanelAsync(string output)
    {
        var editor = new LatexEditor();
        var window = new Window { Content = editor, Width = 800, Height = 400, ShowInTaskbar = false };
        var first = new ProjectFile(Path.Combine(output, "replace.tex"), "replace.tex", ProjectFileKind.Tex, "cat cat cat");
        var other = new ProjectFile(Path.Combine(output, "other.tex"), "other.tex", ProjectFileKind.Tex, "cat in another file");
        editor.ShowFile(other, other.Content); var otherDocument = editor.Document;
        editor.ShowFile(first, first.Content);
        try
        {
            window.Show(); editor.ExecuteCommand("replace");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var search = (Control)Field(editor, "_search")!;
            var input = (TextBox)search.Template.FindName("PART_searchTextBox", search);
            var replacement = Descendants<TextBox>(search).Single(box => box.Name == "ReplaceTextBox");
            var current = Descendants<Button>(search).Single(button => button.Name == "ReplaceCurrentButton");
            var all = Descendants<Button>(search).Single(button => button.Name == "ReplaceAllButton");
            var toggle = (Button)search.Template.FindName("PART_replaceToggle", search);
            ((RoutedCommand)toggle.Command).Execute(null, toggle);
            if (replacement.IsVisible) throw new Exception("Find-panel toggle did not collapse replacement");
            ((RoutedCommand)toggle.Command).Execute(null, toggle);
            if (!replacement.IsVisible) throw new Exception("Find-panel toggle did not expand replacement");
            input.Text = "cat"; replacement.Text = "dog";
            editor.Select(0, 3);
            current.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (editor.Text != "dog cat cat") throw new Exception("Replace button did not replace exactly the current match");
            if (!editor.IsKeyboardFocusWithin || replacement.IsKeyboardFocusWithin) throw new Exception("Replacement did not return undo focus to the document");
            ApplicationCommands.Undo.Execute(null, Keyboard.FocusedElement);
            if (editor.Text != first.Content || editor.Document.UndoStack.CanUndo)
                throw new Exception("Replace current was not exactly one undo unit");
            all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (editor.Text != "dog dog dog" || otherDocument.Text != other.Content)
                throw new Exception("Replace all did not stay within the current document");
            ApplicationCommands.Undo.Execute(null, Keyboard.FocusedElement);
            if (editor.Text != first.Content || editor.Document.UndoStack.CanUndo)
                throw new Exception("Replace all was not exactly one undo unit");
            // The real input binding must report a bad regex without modifying the document.
            search.GetType().GetProperty("UseRegex")!.SetValue(search, true);
            input.Text = "[";
            all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (editor.Text != first.Content || editor.Document.UndoStack.CanUndo)
                throw new Exception("Invalid regex in the replacement UI modified the document");
            input.Text = "cat";
            search.GetType().GetProperty("UseRegex")!.SetValue(search, false);
            async Task CheckAndCapture(string name)
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                foreach (var control in new FrameworkElement[] { input, replacement, current, all })
                {
                    var bounds = control.TransformToAncestor(editor).TransformBounds(new Rect(new Point(), control.RenderSize));
                    var visible = VisibleBounds(control, editor);
                    if (!control.IsVisible || bounds.Width < 20 || bounds.Height < 16 || visible.IsEmpty ||
                        visible.Width < bounds.Width - 1 || visible.Height < bounds.Height - 1)
                        throw new Exception($"{name}: replacement control {control.Name} is empty or clipped");
                }
                Capture(window, Path.Combine(output, name + ".png"));
            }
            await CheckAndCapture("replace-normal");
            editor.Width = 300; editor.HorizontalAlignment = HorizontalAlignment.Left; editor.FontSize = 24;
            window.Width = 340;
            await CheckAndCapture("replace-narrow-large-font");
            Console.WriteLine("PASS: replace controls, current-file replacement, grouped Undo and normal/narrow rendering");
        }
        finally { window.Close(); }
    }

    private static Rect VisibleBounds(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(new Point(), element.RenderSize));
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement visual)
            {
                if (!visual.IsVisible || visual.Opacity == 0) return Rect.Empty;
                var transform = visual.TransformToAncestor(root);
                if (VisualTreeHelper.GetClip(visual) is { } clip) bounds.Intersect(transform.TransformBounds(clip.Bounds));
                if (visual.ClipToBounds || ReferenceEquals(visual, root)) bounds.Intersect(transform.TransformBounds(new Rect(new Point(), visual.RenderSize)));
            }
            if (ReferenceEquals(current, root)) break;
        }
        return bounds;
    }

    private static async Task CheckNavigationAsync(MainWindow window, string output)
    {
        var rail = (FrameworkElement)window.FindName("WorkspaceRail");
        var sidebar = (FrameworkElement)window.FindName("SidebarPanel");
        var column = (ColumnDefinition)window.FindName("SidebarColumn");
        var menu = (Menu)window.FindName("MainMenu");
        var editor = (LatexEditor)window.FindName("Editor");
        var search = (TextBox)window.FindName("SearchBox");
        var buttons = Descendants<Button>(rail).ToArray();
        window.UpdateLayout();
        if (buttons.Length != 7 || window.FindName("CollapseWorkspaceButton") is not null)
            throw new Exception("Permanent rail must contain six panel functions plus AI marking and no collapse button");
        var positions = buttons.ToDictionary(b => b, b => b.TranslatePoint(new Point(), window));
        var path = editor.CurrentFilePath; var text = editor.Text;
        column.Width = new GridLength(315); window.UpdateLayout();
        foreach (var mode in new[] { "outline", "files", "search", "bookmarks", "labels", "todos" })
        {
            if ((bool)Field(window, "_workspaceExpanded")!) Invoke(window, "ToggleWorkspace");
            var button = buttons.Single(b => Equals(b.Tag, mode));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            if (!sidebar.IsVisible || (string)Field(window, "_sidebar")! != mode || Math.Abs(column.ActualWidth - 315) > 1)
                throw new Exception(mode + " single click did not expand its panel");
            if (mode == "search") search.Text = "section";
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            if (sidebar.IsVisible || !rail.IsVisible || Math.Abs(column.ActualWidth - 42) > 1)
                throw new Exception(mode + " second single click did not collapse its panel");
            if (buttons.Any(b => (b.TranslatePoint(new Point(), window) - positions[b]).Length > 1))
                throw new Exception("Function icons moved during collapse/expand");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        }
        buttons.Single(b => Equals(b.Tag, "search")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        if (!sidebar.IsVisible || search.Text != "section" || editor.CurrentFilePath != path || editor.Text != text)
            throw new Exception("Switching functions discarded source or search state");
        var ctrlB = CreateKey(window, Key.B, Keyboard.PreviewKeyDownEvent);
        await CallAsync(window, "HandleShortcutAsync", ctrlB, ModifierKeys.Control);
        if (!ctrlB.Handled || sidebar.IsVisible) throw new Exception("Ctrl+B did not collapse sidebar");
        await CallAsync(window, "HandleShortcutAsync", CreateKey(window, Key.B, Keyboard.PreviewKeyDownEvent), ModifierKeys.Control);
        if (!sidebar.IsVisible) throw new Exception("Ctrl+B did not expand sidebar");
        foreach (var iteration in new[] { 0, 1, 2 })
        {
            window.RaiseEvent(CreateKey(window, Key.LeftAlt, Keyboard.PreviewKeyDownEvent));
            window.RaiseEvent(CreateKey(window, Key.LeftAlt, Keyboard.PreviewKeyUpEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (!menu.IsVisible) throw new Exception("Alt hid the persistent main menu");
        }
        Invoke(window, "HideMainMenu", true);
        if (!menu.IsVisible) throw new Exception("Closing menus hid the main menu bar");
        editor.TextArea.Focus();
        if ((bool)Invoke(window, "ProcessMenuKeyDown", Key.Escape, ModifierKeys.None, false)!)
            throw new Exception("Persistent menu consumed editor Escape with no menu active");
        Capture(window, Path.Combine(output, "permanent-rail-menu.png"));
        await CheckZoomControlsAsync(window, output);
        Console.WriteLine("PASS: persistent menu, six stationary single-click function toggles, Ctrl+B and preserved source/search state");
        search.Clear();
        buttons.Single(b => Equals(b.Tag, "outline")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task CheckViewportFollowingAsync(MainWindow window, string root, string output)
    {
        var engine = Path.Combine(root, ".tools", "tectonic", "tectonic.exe");
        if (!File.Exists(engine)) throw new Exception("Viewport regression requires the local .tools/tectonic/tectonic.exe fixture engine");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var fixture = Path.Combine(output, "viewport-project"); Directory.CreateDirectory(fixture);
        var main = Path.Combine(fixture, "main.tex");
        var lines = new List<string> { "\\documentclass{article}", "\\pagestyle{empty}", "\\begin{document}" };
        for (var page = 1; page <= 8; page++)
        {
            if (page > 1) lines.Add("\\newpage");
            for (var row = 1; row <= 40; row++)
                lines.Add($"\\noindent Page {page}, row {row:00}. View synchronization.\\par");
        }
        lines.Add("\\end{document}");
        File.WriteAllText(main, string.Join('\n', lines) + "\n");
        var settingsField = window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalSettings = (AppSettings)settingsField.GetValue(window)!;
        // An explicit missing command selects the real SyncTeX parser without a machine-specific CLI.
        var settings = new AppSettings
        {
            LivePreview = false, FollowMode = PreviewFollowMode.Viewport, Engine = TexEngine.Tectonic, EnginePath = engine,
            SynctexPath = Path.Combine(fixture, "no-external-synctex.exe")
        };
        settingsField.SetValue(window, settings);
        try
        {
            await CallAsync(window, "OpenAsync", main);
            await CallAsync(window, "BuildAsync");
            var editor = (LatexEditor)window.FindName("Editor");
            var preview = (PdfPreview)window.FindName("Preview");
            if (((AppSettings)Field(window, "_settings")!).FollowMode != PreviewFollowMode.Viewport) throw new Exception("Incorrect follow mode");
            var pdfScroll = (ScrollViewer)Field(preview, "_scroll")!;
            var project = (TexProject)Field(window, "_project")!;
            var pdf = (string?)Field(window, "_pdfPath");
            if (pdf == null || preview.PageCount != 8)
                throw new Exception("Viewport fixture must produce eight real PDF pages: " + ((TextBox)window.FindName("LogText")).Text);
            await CheckPdfColorsAsync(window, output);
            await CheckZoomControlsAsync(window, output);
            var sync = new SyncTexService();
            void SelectMode(PreviewFollowMode mode)
            {
                Invoke(window, "ChangeFollowMode", mode);
                if (((AppSettings)settingsField.GetValue(window)!).FollowMode != mode)
                    throw new Exception("Preview follow mode did not take effect from its toolbar: " + mode);
                var profile = Environment.GetEnvironmentVariable("TEXEED_HOME") ?? throw new Exception("Test settings profile is not isolated");
                var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(profile, "settings.json")));
                if (persisted?.FollowMode != mode) throw new Exception("Toolbar follow mode was not persisted immediately: " + mode);
            }
            async Task<PdfLocation> MapAsync(SourceLocation source) =>
                await (((AppSettings)Field(window, "_settings")!).FollowMode == PreviewFollowMode.Viewport
                    ? sync.ForwardViewportAsync(project, pdf, source, settings)
                    : sync.ForwardAsync(project, pdf, source, settings)) ?? throw new Exception($"No SyncTeX mapping for {source.Line}");
            async Task<SourceLocation> ScrollSourceAsync(int topLine)
            {
                editor.ScrollToVerticalOffset((topLine - 1) * editor.TextArea.TextView.DefaultLineHeight);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                return editor.ViewportLocation ?? throw new Exception("Visible source viewport has no anchor");
            }
            async Task AwaitLocationAsync(SourceLocation source, double viewportFraction, bool centerBounds = false)
            {
                var point = await MapAsync(source);
                var expected = PdfOffset(preview, centerBounds ? point with { Y = point.Y + point.Height / 2 } : point, viewportFraction);
                await WaitUntilAsync(() => Math.Abs(pdfScroll.VerticalOffset - expected) < 2 && Field(preview, "_highlight") == null,
                    $"Preview did not follow source line {source.Line} without a highlight at {viewportFraction:P0} height (expected offset {expected:0.0})");
            }
            Task AwaitAnchorAsync(SourceLocation source) => AwaitLocationAsync(source, .5);

            // Exercise the outline handler with nearby locations on the same PDF
            // page: neither a page change nor a second click may be required.
            var nearby = new[] { new SourceLocation(main, 100), new SourceLocation(main, 103) };
            if ((await MapAsync(nearby[0])).Page != (await MapAsync(nearby[1])).Page)
                throw new Exception("Nearby outline targets must share a PDF page");
            foreach (var mode in new[] { PreviewFollowMode.Viewport, PreviewFollowMode.Cursor, PreviewFollowMode.None })
            {
                SelectMode(mode);
                foreach (var source in nearby)
                {
                    var before = pdfScroll.VerticalOffset;
                    Invoke(window, "Outline_NavigateRequested", window, source);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    window.UpdateLayout();
                    if (editor.CurrentLocation?.Line != source.Line || editor.ViewportLocation?.Line != source.Line)
                        throw new Exception($"Single outline click did not center nearby source line {source.Line} in mode {mode}");
                    if (mode == PreviewFollowMode.None)
                        await AssertPdfOffsetStableAsync(pdfScroll, before, "Outline navigation overrode None mode");
                    else
                        await AwaitLocationAsync(source, .5, centerBounds: mode == PreviewFollowMode.Cursor);
                }
                if (mode != PreviewFollowMode.None)
                {
                    Invoke(window, "CancelPendingNavigation");
                    pdfScroll.ScrollToVerticalOffset(pdfScroll.VerticalOffset + 100);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Invoke(window, "Outline_NavigateRequested", window, nearby[1]);
                    await AwaitLocationAsync(nearby[1], .5, centerBounds: mode == PreviewFollowMode.Cursor);
                }
            }
            SelectMode(PreviewFollowMode.Viewport);
            Console.WriteLine("PASS: one outline click centers same-screen source targets and same-page PDF positions; repeated clicks resync, while None mode stays still");

            editor.CaretOffset = editor.Document.GetLineByNumber(5).Offset;
            await ScrollSourceAsync(1);
            await Task.Delay(600);
            var initialOffset = pdfScroll.VerticalOffset;
            var originalCaret = editor.CaretOffset;
            var anchor = await ScrollSourceAsync(95);
            if (editor.CaretOffset != originalCaret) throw new Exception("Scrolling the source fixture unexpectedly moved the caret");
            await AwaitAnchorAsync(anchor);
            if (Math.Abs(pdfScroll.VerticalOffset - initialOffset) < pdfScroll.ViewportHeight)
                throw new Exception("Scrolling with a stationary caret did not move the PDF to a different page");
            await WaitForPreviewRenderedAsync(window);
            Capture(window, Path.Combine(output, "viewport-following.png"));
            Console.WriteLine("PASS: source scrolling moves the real PDF to the visible source center while the caret stays fixed; no automatic highlight");

            var sourceOffset = editor.VerticalOffset;
            var pdfOffset = pdfScroll.VerticalOffset;
            editor.CaretOffset = editor.Document.GetLineByNumber(anchor.Line + 2).Offset;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (Math.Abs(editor.VerticalOffset - sourceOffset) > 1) throw new Exception("Caret-only regression accidentally scrolled the source view");
            await AssertPdfOffsetStableAsync(pdfScroll, pdfOffset, "Moving only the caret within the viewport moved the PDF");
            Console.WriteLine("PASS: moving the caret within an unchanged source view does not move the PDF");

            await ScrollSourceAsync(175);
            await ScrollSourceAsync(35);
            anchor = await ScrollSourceAsync(255);
            await AwaitAnchorAsync(anchor);
            await AssertPdfOffsetStableAsync(pdfScroll, pdfScroll.VerticalOffset, "An earlier scroll request overwrote the latest viewport position");
            Console.WriteLine("PASS: rapid source scrolling settles at the final viewport, without stale jumps");

            originalCaret = editor.CaretOffset;
            foreach (var crop in new[] { true, false })
            {
                preview.AutoCrop = crop;
                await WaitForPreviewRenderedAsync(window);
                var maxOffset = editor.ExtentHeight - editor.ViewportHeight;
                var start = Math.Max(0, maxOffset - editor.ViewportHeight);
                var previousLine = 0;
                for (var step = 0; step <= 20; step++)
                {
                    editor.ScrollToVerticalOffset(start + (maxOffset - start) * step / 20);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    window.UpdateLayout();
                    anchor = editor.ViewportLocation!;
                    if (anchor.Line < previousLine) throw new Exception("Document-end follow anchor moved backward while scrolling down");
                    previousLine = anchor.Line;
                }
                editor.ScrollToVerticalOffset(editor.ExtentHeight);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                anchor = editor.ViewportLocation!;
                if (anchor.Line != editor.Document.LineCount || (await MapAsync(anchor)).Page != preview.PageCount)
                    throw new Exception("Bottom viewport anchor cannot reach final source line / final PDF page");
                var textView = editor.TextArea.TextView;
                var finalRowY = textView.VisualLines[^1].VisualTop - textView.ScrollOffset.Y;
                if (finalRowY < -1 || finalRowY > textView.ActualHeight / 4)
                    throw new Exception($"Overscrolling must bring the final source row into the top quarter of the editor (y={finalRowY:F1})");
                await AwaitAnchorAsync(anchor);
                await AssertPdfOffsetStableAsync(pdfScroll, pdfScroll.VerticalOffset, "Final page automatic follow did not stay at document end");
                if (editor.CaretOffset != originalCaret) throw new Exception("Document-end follow moved the caret");
            }
            Capture(window, Path.Combine(output, "editor-overscroll.png"));
            Console.WriteLine("PASS: native overscroll brings final source row near the top and keeps final PDF page visible, with/without cropping");

            SelectMode(PreviewFollowMode.Cursor);
            await AwaitLocationAsync(editor.CurrentLocation!, .5, centerBounds: true);
            pdfOffset = pdfScroll.VerticalOffset;
            originalCaret = editor.CaretOffset;
            await ScrollSourceAsync(155);
            if (editor.CaretOffset != originalCaret) throw new Exception("Cursor-mode source scrolling moved the fixture caret");
            await AssertPdfOffsetStableAsync(pdfScroll, pdfOffset, "Cursor mode incorrectly followed source scrolling while the caret stayed fixed");
            editor.CaretOffset = editor.Document.GetLineByNumber(260).Offset;
            await AwaitLocationAsync(editor.CurrentLocation!, .5, centerBounds: true);
            if (Math.Abs(pdfScroll.VerticalOffset - pdfOffset) < pdfScroll.ViewportHeight)
                throw new Exception("Cursor mode did not move the PDF after moving the caret to a different page");
            Console.WriteLine("PASS: toolbar cursor mode follows caret movement without following source scroll or flashing highlights");

            // Changing the caret queues automatic cursor synchronization synchronously. Switching
            // the toolbar to None in the same dispatcher turn must cancel that pending navigation.
            pdfOffset = pdfScroll.VerticalOffset;
            editor.CaretOffset = editor.Document.GetLineByNumber(290).Offset;
            SelectMode(PreviewFollowMode.None);
            await AssertPdfOffsetStableAsync(pdfScroll, pdfOffset, "Switching to None did not cancel pending cursor synchronization");
            pdfOffset = pdfScroll.VerticalOffset;
            await ScrollSourceAsync(135);
            await AssertPdfOffsetStableAsync(pdfScroll, pdfOffset, "Disabling view following did not stop automatic PDF navigation");
            Console.WriteLine("PASS: toolbar None mode cancels pending synchronization and keeps the PDF still during source scrolling");

            // Explicit navigation has the same caret meaning in every mode and cancels a
            // just-scheduled automatic follow instead of letting it undo the Ctrl+J request.
            foreach (var mode in new[] { PreviewFollowMode.None, PreviewFollowMode.Cursor, PreviewFollowMode.Viewport })
            {
                SelectMode(PreviewFollowMode.None);
                var caretLocation = new SourceLocation(main, 8);
                editor.CaretOffset = editor.Document.GetLineByNumber(caretLocation.Line).Offset;
                await ScrollSourceAsync(215);
                var caretPoint = await MapAsync(caretLocation);
                SelectMode(mode);
                var shortcut = CreateKey(window, Key.J, Keyboard.PreviewKeyDownEvent);
                await CallAsync(window, "HandleShortcutAsync", shortcut, ModifierKeys.Control);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                if (!shortcut.Handled || Field(preview, "_highlight") is not PdfLocation highlighted || highlighted != caretPoint)
                    throw new Exception($"Ctrl+J did not explicitly highlight the caret's PDF location in {mode} mode");
                var expected = PdfOffset(preview, mode == PreviewFollowMode.Cursor ? caretPoint with { Y = caretPoint.Y + caretPoint.Height / 2 } : caretPoint,
                    mode == PreviewFollowMode.Cursor ? .5 : .35);
                if (Math.Abs(pdfScroll.VerticalOffset - expected) > 2)
                    throw new Exception($"Ctrl+J did not scroll to the caret's PDF location in {mode} mode");
                await AssertPdfOffsetStableAsync(pdfScroll, expected, $"Automatic {mode} mode synchronization overrode Ctrl+J");
            }
            Console.WriteLine("PASS: Ctrl+J follows and highlights the caret in all three modes; automatic synchronization does not override it");

            SelectMode(PreviewFollowMode.Viewport);
            anchor = await ScrollSourceAsync(255);
            await AwaitAnchorAsync(anchor);
            var inversePoint = await MapAsync(new SourceLocation(main, 120));
            preview.ShowLocation(inversePoint);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            pdfOffset = pdfScroll.VerticalOffset;
            var previousCaretLine = editor.CurrentLocation!.Line;
            await CallAsync(window, "InverseAsync", inversePoint);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (editor.CurrentLocation!.Line == previousCaretLine || editor.CurrentFilePath != main)
                throw new Exception("PDF inverse navigation did not move to the corresponding source");
            await AssertPdfOffsetStableAsync(pdfScroll, pdfOffset, "Inverse navigation caused the PDF to jump back to the editor viewport center");
            Console.WriteLine("PASS: inverse SyncTeX changes the source view without feeding an automatic jump back into the PDF");

            SelectMode(PreviewFollowMode.Cursor);
            await AwaitLocationAsync(editor.CurrentLocation!, .5, centerBounds: true);
            inversePoint = await MapAsync(new SourceLocation(main, 205));
            preview.ShowLocation(inversePoint);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            pdfOffset = pdfScroll.VerticalOffset;
            previousCaretLine = editor.CurrentLocation!.Line;
            await CallAsync(window, "InverseAsync", inversePoint);
            if (editor.CurrentLocation!.Line == previousCaretLine)
                throw new Exception("Cursor-mode inverse navigation did not move the source caret");
            await AssertPdfOffsetStableAsync(pdfScroll, pdfOffset, "Cursor mode fed inverse navigation back into an automatic PDF jump");
            Console.WriteLine("PASS: cursor mode also suppresses automatic feedback during inverse SyncTeX");
            SelectMode(PreviewFollowMode.Viewport);
            await AwaitAnchorAsync(editor.ViewportLocation!);

            // Start a pending editor follow, then exercise the PDF's actual interaction event.
            // A plain ScrollToVerticalOffset alone is programmatic and cannot prove cancellation.
            await ScrollSourceAsync(225);
            preview.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            pdfScroll.ScrollToVerticalOffset(Math.Min(pdfScroll.ScrollableHeight, pdfScroll.VerticalOffset + pdfScroll.ViewportHeight * 1.5));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            pdfOffset = pdfScroll.VerticalOffset;
            await AssertPdfOffsetStableAsync(pdfScroll, pdfOffset, "Manual PDF scrolling was pulled back by a pending source-view follow");
            anchor = await ScrollSourceAsync(275);
            await AwaitAnchorAsync(anchor);
            Console.WriteLine("PASS: manual PDF scrolling cancels a pending automatic follow; a later source scroll resumes following");

            var topLine = 145;
            foreach (var direction in new[] { 1, -1, 0 })
            {
                await ScrollSourceAsync(topLine);
                if (direction == 0) Invoke(window, "AutoWidth_Click", window, new RoutedEventArgs());
                else Invoke(window, "SetManualZoom", preview.Zoom * (direction > 0 ? 1.1 : 1 / 1.1));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                await AssertPdfOffsetStableAsync(pdfScroll, pdfScroll.VerticalOffset,
                    "A pending source-view follow overrode the PDF zoom/fit-width action " + direction);
                topLine += 25;
            }
            Console.WriteLine("PASS: PDF zoom and fit-width toolbar actions cancel pending source-view navigation");
            await CheckPreviewModeToolbarAsync(window, output);
            CheckSettingsFollowModes(window, output);
        }
        finally { settingsField.SetValue(window, originalSettings); }
    }

    private static async Task CheckPreviewModeToolbarAsync(MainWindow window, string output)
    {
        var column = WorkspaceColumn(window, 4); var original = column.Width;
        try
        {
            column.Width = new GridLength(300); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var strip = (EditorTabStrip)window.FindName("RightFileTabs");
            var row = ((StackPanel)strip.FindName("Items")).Children.Cast<Border>().Single();
            var bounds = row.TransformToAncestor(strip).TransformBounds(new Rect(row.RenderSize));
            if (bounds.Right > strip.ActualWidth || !row.IsVisible) throw new Exception("Preview tab/gear are clipped at 300 DIP");
            Capture(window, Path.Combine(output, "preview-tab-narrow.png"));
            Console.WriteLine("PASS: preview tab and gear fit a narrow group");
        }
        finally { column.Width = original; window.UpdateLayout(); }
    }
    private static ColumnDefinition WorkspaceColumn(MainWindow window, int index) => ((Grid)window.FindName("Workspace")).ColumnDefinitions[index];
    private static void CheckSettingsFollowModes(MainWindow owner, string output)
    {
        var dialog = new SettingsWindow(new AppSettings { FollowMode = PreviewFollowMode.Cursor, LivePreview = false, EditorFontFamily = "Arial" }) { Owner = owner };
        Exception? failure = null;
        dialog.Loaded += (_, _) =>
        {
            try
            {
                var modes = (ComboBox)dialog.FindName("FollowModeBox");
                if (modes.Items.Count != 3 || !Equals(modes.SelectedValue, PreviewFollowMode.Cursor))
                    throw new Exception("Settings did not expose three modes and load the existing cursor mode");
                modes.SelectedValue = PreviewFollowMode.None;
                var invert = (CheckBox)dialog.FindName("InvertPdfBox");
                if (invert.IsChecked != true) throw new Exception("PDF inversion should default on in settings");
                invert.IsChecked = false;
                var autoCrop = (CheckBox)dialog.FindName("AutoCropPdfBox");
                if (autoCrop.IsChecked != true) throw new Exception("Automatic cropping should default on");
                autoCrop.IsChecked = false;
                var font = (ComboBox)dialog.FindName("EditorFontBox");
                if (font.Text != "Arial") throw new Exception("Settings did not preserve a proportional editor font");
                font.Text = "Times New Roman";
                Capture(dialog, Path.Combine(output, "settings-follow-mode.png"));
                Descendants<Button>(dialog).Single(button => button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        var accepted = dialog.ShowDialog();
        if (failure != null) throw new Exception("Settings follow-mode regression failed", failure);
        if (accepted != true || dialog.Settings.FollowMode != PreviewFollowMode.None || dialog.Settings.EditorFontFamily != "Times New Roman" || dialog.Settings.InvertPdfColors || dialog.Settings.AutoCropPdf)
            throw new Exception("Saving settings did not retain the selected follow mode");
        Console.WriteLine("PASS: settings loads an existing follow mode, offers all three modes and saves the selected mode");
    }

    private static double PdfOffset(PdfPreview preview, PdfLocation point, double viewportFraction)
    {
        var scroll = (ScrollViewer)Field(preview, "_scroll")!;
        var layouts = ((System.Collections.IEnumerable)Field(preview, "_layouts")!).Cast<object>().ToArray();
        var layout = layouts[point.Page - 1];
        var top = (double)layout.GetType().GetProperty("Top")!.GetValue(layout)!;
        var mapped = (Point)typeof(PdfPreview).GetMethod("PageToDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(preview, [point.Page - 1, new Point(point.X, point.Y)])!;
        var margin = ((Canvas)Field(preview, "_pages")!).Margin.Top;
        return Math.Clamp(margin + top + 1 + mapped.Y - scroll.ViewportHeight * viewportFraction, 0, scroll.ScrollableHeight);
    }

    private static async Task WaitUntilAsync(Func<bool> completed, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!completed())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception(failure);
            await Task.Delay(25);
        }
    }

    private static async Task AssertPdfOffsetStableAsync(ScrollViewer scroll, double expected, string failure)
    {
        // Observe beyond the synchronization interval so an unwanted navigation cannot hide between checks.
        var deadline = DateTime.UtcNow.AddMilliseconds(1000);
        while (DateTime.UtcNow < deadline)
        {
            if (Math.Abs(scroll.VerticalOffset - expected) > 2)
                throw new Exception($"{failure} (was {expected:0.0}, now {scroll.VerticalOffset:0.0})");
            await Task.Delay(25);
        }
    }

    private static async Task WaitForPreviewRenderedAsync(MainWindow window)
    {
        window.UpdateLayout();
        var preview = (PdfPreview)window.FindName("Preview");
        if (preview.PageCount == 0) return;
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scroll = (ScrollViewer)Field(preview, "_scroll")!;
        void CheckRendered()
        {
            if ((bool)Field(preview, "_rendering")! || ((DispatcherTimer)Field(preview, "_renderTimer")!).IsEnabled) return;
            var viewport = new Rect(new Point(), scroll.RenderSize);
            var visiblePages = Descendants<Image>(preview).Where(image => image.ActualWidth > 0 && image.ActualHeight > 0 &&
                image.TransformToAncestor(scroll).TransformBounds(new Rect(new Point(), image.RenderSize)).IntersectsWith(viewport)).ToArray();
            if (visiblePages.Length > 0 && visiblePages.All(image => image.Source is BitmapSource)) rendered.TrySetResult();
        }
        EventHandler onFrame = (_, _) => CheckRendered();
        CompositionTarget.Rendering += onFrame;
        try
        {
            CheckRendered();
            // Completion depends on the current visible pages acquiring rasters, not an assumed render delay.
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { CompositionTarget.Rendering -= onFrame; }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T matched) yield return matched;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var visual = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(visual.ActualWidth + visual.Margin.Left + visual.Margin.Right);
        var height = (int)Math.Ceiling(visual.ActualHeight + visual.Margin.Top + visual.Margin.Bottom);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
        target.Render(background);
        target.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
        using var file = File.Create(path); encoder.Save(file);
    }
}
