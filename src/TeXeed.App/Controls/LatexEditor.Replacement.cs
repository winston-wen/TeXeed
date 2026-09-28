using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Search;

namespace TeXeed.App.Controls;

public sealed record EditorReplacementResult(int Count, string Message);

public sealed partial class LatexEditor
{
    /// <summary>Replaces a selected search match, or selects the next match first.</summary>
    public EditorReplacementResult ReplaceCurrent(string replacement) => ReplaceMatches(replacement, false);

    /// <summary>Replaces every match in the current file as one undoable edit.</summary>
    public EditorReplacementResult ReplaceAll(string replacement) => ReplaceMatches(replacement, true);

    private EditorReplacementResult ReplaceMatches(string replacement, bool replaceAll)
    {
        VerifyAccess();
        if (_changingDocument || CurrentFilePath is null || Document is null)
            return new(0, "请先打开一个可编辑的文件。");
        if (IsReadOnly) return new(0, "当前文件为只读，无法替换。");
        if (string.IsNullOrEmpty(_search.SearchPattern)) return new(0, "请输入查找内容。");

        var document = Document;
        var path = CurrentFilePath;
        var version = document.Version;
        ReplacementMatch[] matches;
        try
        {
            ArgumentNullException.ThrowIfNull(replacement);
            var useRegex = _search.UseRegex;
            var strategy = SearchStrategyFactory.Create(_search.SearchPattern, !_search.MatchCase,
                _search.WholeWords, useRegex ? SearchMode.RegEx : SearchMode.Normal);
            var snapshot = document.CreateSnapshot();
            // Materialize matches and expansions before the first edit: malformed patterns or
            // replacement expressions must never leave a partially replaced document.
            matches = strategy.FindAll(snapshot, 0, snapshot.TextLength)
                .Select(match => new ReplacementMatch(match.Offset, match.Length,
                    useRegex ? match.ReplaceWith(replacement) : replacement))
                .ToArray();
        }
        catch (RegexMatchTimeoutException)
        {
            return new(0, "查找超时，请简化正则表达式后重试。");
        }
        catch (SearchPatternException ex)
        {
            return new(0, $"查找表达式无效：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return new(0, $"查找或替换表达式无效：{ex.Message}");
        }

        if (matches.Length == 0) return new(0, "未找到匹配项。");
        if (!IsReplacementDocument(document, path) || version.CompareAge(document.Version) != 0)
            return new(0, "文件已切换或修改，请重新替换。");

        var selectedIndex = Array.FindIndex(matches,
            match => match.Offset == SelectionStart && match.Length == SelectionLength);
        if (!replaceAll && selectedIndex < 0)
        {
            var start = Math.Max(CaretOffset, SelectionStart + SelectionLength);
            var next = matches.FirstOrDefault(match => match.Offset >= start) ?? matches[0];
            SelectReplacementMatch(document, path, next.Offset, next.Length);
            return new(0, "已选中下一处匹配，点击“替换当前”进行替换。");
        }

        var replacements = replaceAll ? matches : [matches[selectedIndex]];
        if (replacements.Any(match => !CanReplaceMatch(match)))
            return new(0, "匹配内容包含只读区域，无法替换。");

        CloseCompletion();
        var count = 0;
        using (document.RunUpdate())
        {
            // Working backwards keeps all snapshot offsets valid, including empty regex matches.
            foreach (var match in replacements.Reverse())
            {
                if (!IsReplacementDocument(document, path)) break;
                document.Replace(match.Offset, match.Length, match.Replacement);
                count++;
            }
        }

        if (!replaceAll && count == 1 && IsReplacementDocument(document, path))
        {
            var replaced = matches[selectedIndex];
            if (matches.Length > 1)
            {
                var next = matches[(selectedIndex + 1) % matches.Length];
                var offset = next.Offset + (next.Offset >= replaced.Offset + replaced.Length
                    ? replaced.Replacement.Length - replaced.Length : 0);
                SelectReplacementMatch(document, path, offset, next.Length);
            }
            else SelectReplacementMatch(document, path, replaced.Offset + replaced.Replacement.Length, 0);
        }
        return new(count, count == replacements.Length
            ? $"已替换 {count} 处。" : $"已替换 {count} 处；文件已切换，后续替换已停止。");
    }

    private bool IsReplacementDocument(TextDocument document, string path)
        => !_changingDocument && !IsReadOnly && ReferenceEquals(Document, document)
            && string.Equals(CurrentFilePath, path, StringComparison.OrdinalIgnoreCase);

    private bool CanReplaceMatch(ReplacementMatch match)
    {
        var provider = TextArea.ReadOnlySectionProvider;
        if (match.Replacement.Length > 0 && !provider.CanInsert(match.Offset)) return false;
        if (match.Length == 0) return true;
        var deletable = provider.GetDeletableSegments(new TextSegment { StartOffset = match.Offset, Length = match.Length }).ToArray();
        return deletable.Length == 1 && deletable[0].Offset == match.Offset && deletable[0].Length == match.Length;
    }

    private void SelectReplacementMatch(TextDocument document, string path, int offset, int length)
    {
        if (!IsReplacementDocument(document, path)) return;
        offset = Math.Clamp(offset, 0, document.TextLength);
        length = Math.Clamp(length, 0, document.TextLength - offset);
        Select(offset, length);
        if (!IsReplacementDocument(document, path)) return;
        CaretOffset = offset + length;
        if (IsReplacementDocument(document, path)) TextArea.Caret.BringCaretToView();
    }

    private sealed record ReplacementMatch(int Offset, int Length, string Replacement);
}
