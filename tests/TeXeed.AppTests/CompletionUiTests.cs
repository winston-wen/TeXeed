using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.CodeCompletion;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckCompletionKeysAsync()
    {
        var editor = new LatexEditor();
        var host = new Window { Content = editor, Width = 700, Height = 420, Title = "TeXeed completion regression" };
        host.Show(); host.Activate(); editor.TextArea.Focus();
        try
        {
            async Task Open(string text, string choice)
            {
                Invoke(editor, "CloseCompletion");
                var file = new ProjectFile("completion.tex", "completion.tex", ProjectFileKind.Tex, text);
                editor.ShowFile(file, text); editor.CaretOffset = text.Length;
                editor.SetProjectFiles([file, new ProjectFile("labels.tex", "labels.tex", ProjectFileKind.Tex, "\\label{sec:test}")]);
                host.UpdateLayout(); editor.TextArea.Focus();
                Invoke(editor, "ShowCompletion", true);
                var popup = (CompletionWindow?)Field(editor, "_completion") ?? throw new Exception("No completion window");
                popup.CompletionList.SelectItem(choice);
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            }
            async Task Press(Key key)
            {
                var args = CreateKey(host, key, Keyboard.PreviewKeyDownEvent);
                editor.TextArea.RaiseEvent(args);
                if (!args.Handled) throw new Exception("Completion did not handle " + key);
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            }
            foreach (var key in new[] { Key.Enter, Key.Tab })
            {
                foreach (var prefix in new[] { "\\newp", "\\newpage" })
                {
                    await Open(prefix, "newpage"); await Press(key);
                    if (editor.Text != "\\newpage" || editor.CaretOffset != editor.Text.Length)
                        throw new Exception("Completion inserted wrong command or moved the caret");
                    if (Field(editor, "_completion") != null) throw new Exception("Accepted newpage completion reopened after " + key);
                }
                await Open("\\sect", "section"); await Press(key);
                if (editor.Text != "\\section{}" || editor.CaretOffset != editor.Text.Length - 1 || Field(editor, "_completion") != null)
                    throw new Exception("Section completion lost its placeholder or reopened");
                editor.Undo();
                if (editor.Text != "\\sect") throw new Exception("Completion was not one undo step");
                await Open("\\re", "ref"); await Press(key);
                var references = (CompletionWindow?)Field(editor, "_completion") ?? throw new Exception("Reference completion did not chain to labels");
                references.CompletionList.SelectItem("sec:test"); await Press(key);
                if (editor.Text != "\\ref{sec:test}" || Field(editor, "_completion") != null)
                    throw new Exception("Reference completion did not insert the label and close");
            }
            await Open("\\newp", "newpage"); await Press(Key.Escape);
            if (editor.Text != "\\newp" || Field(editor, "_completion") != null) throw new Exception("Escape changed the document");
            Console.WriteLine("PASS: Enter/Tab accept partial and exact commands without reopening; reference chaining, placeholder, Undo and Escape preserved");
        }
        finally { Invoke(editor, "CloseCompletion"); host.Close(); }
    }
}
