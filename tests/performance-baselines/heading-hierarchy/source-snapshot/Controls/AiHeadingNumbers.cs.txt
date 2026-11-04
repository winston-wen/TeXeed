namespace TeXeed.App.Controls;

// Counters are per turn, shared in document order across answer segments.
internal sealed class AiHeadingNumbers
{
    private readonly int[] _counts;

    internal AiHeadingNumbers() : this(new int[6]) { }
    internal AiHeadingNumbers(int[] counts) => _counts = (int[])counts.Clone();
    internal int[] Snapshot() => (int[])_counts.Clone();

    internal string Next(int level)
    {
        if (level is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(level));
        _counts[level - 1]++;
        Array.Clear(_counts, level, _counts.Length - level);
        Span<char> buffer = stackalloc char[6 * 11 + 5];
        var length = 0;
        for (var index = 0; index < level; index++)
        {
            if (index > 0) buffer[length++] = '.';
            _counts[index].TryFormat(buffer[length..], out var written);
            length += written;
        }
        return new string(buffer[..length]);
    }
}
