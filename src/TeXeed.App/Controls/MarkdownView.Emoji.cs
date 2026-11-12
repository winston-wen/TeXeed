using System.Globalization;
using System.Windows.Documents;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private Span? _streamTail;
    private Run? _streamCommittedText;
    private string _streamTailText = "";

    private void ResetStreamEmoji() { _streamTail = null; _streamCommittedText = null; _streamTailText = ""; }

    private void AppendStreamEmoji(Paragraph paragraph, string next)
    {
        if (_streamTail is null || !next.StartsWith(_streamText, StringComparison.Ordinal))
        {
            paragraph.Inlines.Clear();
            _streamTail = new Span();
            paragraph.Inlines.Add(_streamTail);
            _streamCommittedText = null;
            _streamTailText = "";
            _streamText = "";
        }
        var pending = _streamTailText + next[_streamText.Length..];
        var boundaries = StringInfo.ParseCombiningCharacters(pending);
        var tail = boundaries.Length == 0 ? 0 : boundaries[^1];
        var committed = pending[..tail];
        if (committed.Length > 0)
        {
            var result = new Span();
            AddEmojiText(result.Inlines, committed);
            while (result.Inlines.FirstInline is { } inline)
            {
                result.Inlines.Remove(inline);
                if (inline is Run run && _streamCommittedText is not null) _streamCommittedText.ContentEnd.InsertTextInRun(run.Text);
                else
                {
                    paragraph.Inlines.InsertBefore(_streamTail, inline);
                    _streamCommittedText = inline as Run;
                }
            }
        }
        _streamTailText = pending[tail..];
        _streamTail.Inlines.Clear();
        if (_streamTailText.Length > 0) AddEmojiText(_streamTail.Inlines, _streamTailText);
        _streamText = next;
    }
}
