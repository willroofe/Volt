namespace Volt;

public sealed class LanguagePairIndex
{
    public static LanguagePairIndex Empty { get; } = new(Array.Empty<LanguagePairHighlight>());

    private readonly IReadOnlyList<Node> _roots;

    public LanguagePairIndex(IReadOnlyList<LanguagePairHighlight> pairs)
    {
        Pairs = pairs;
        _roots = BuildTree(pairs);
    }

    public IReadOnlyList<LanguagePairHighlight> Pairs { get; }

    public IReadOnlyList<LanguagePairHighlight> GetMatchingPairs(TextPosition caret)
    {
        if (_roots.Count == 0)
            return Array.Empty<LanguagePairHighlight>();

        var matches = new List<LanguagePairHighlight>();
        AddMatchingPath(_roots, caret, matches);
        return matches.Count == 0 ? Array.Empty<LanguagePairHighlight>() : matches;
    }

    private static IReadOnlyList<Node> BuildTree(IReadOnlyList<LanguagePairHighlight> pairs)
    {
        if (pairs.Count == 0)
            return Array.Empty<Node>();

        var sorted = new List<LanguagePairHighlight>(pairs);
        sorted.Sort(CompareOuterFirst);

        var roots = new List<Node>();
        var stack = new Stack<Node>();
        foreach (LanguagePairHighlight pair in sorted)
        {
            while (stack.Count > 0 && !ContainsRange(stack.Peek().Pair, pair))
                stack.Pop();

            var node = new Node(pair);
            if (stack.TryPeek(out Node? parent))
                parent.AddChild(node);
            else
                roots.Add(node);

            stack.Push(node);
        }

        return roots;
    }

    private static void AddMatchingPath(
        IReadOnlyList<Node> siblings,
        TextPosition caret,
        List<LanguagePairHighlight> matches)
    {
        int candidate = FindLastOpenPairBeforeOrAt(siblings, caret);
        if (candidate < 0)
            return;

        Node node = siblings[candidate];
        if (!ContainsPosition(node.Pair.OpenRange.Start, node.Pair.CloseRange.End, caret))
            return;

        matches.Add(node.Pair);
        AddMatchingPath(node.Children, caret, matches);
    }

    private static int FindLastOpenPairBeforeOrAt(IReadOnlyList<Node> siblings, TextPosition caret)
    {
        int low = 0;
        int high = siblings.Count - 1;
        int result = -1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (ComparePositions(siblings[mid].Pair.OpenRange.Start, caret) <= 0)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private static bool ContainsRange(LanguagePairHighlight outer, LanguagePairHighlight inner) =>
        ComparePositions(inner.OpenRange.Start, outer.OpenRange.Start) >= 0
        && ComparePositions(inner.CloseRange.End, outer.CloseRange.End) <= 0;

    private static int CompareOuterFirst(LanguagePairHighlight left, LanguagePairHighlight right)
    {
        int openComparison = ComparePositions(left.OpenRange.Start, right.OpenRange.Start);
        if (openComparison != 0)
            return openComparison;

        return ComparePositions(right.CloseRange.Start, left.CloseRange.Start);
    }

    private static bool ContainsPosition(TextPosition start, TextPosition end, TextPosition position) =>
        ComparePositions(position, start) >= 0
        && ComparePositions(position, end) <= 0;

    private static int ComparePositions(TextPosition left, TextPosition right)
    {
        int lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0
            ? lineComparison
            : left.Column.CompareTo(right.Column);
    }

    private sealed class Node
    {
        private List<Node>? _children;

        public Node(LanguagePairHighlight pair)
        {
            Pair = pair;
        }

        public LanguagePairHighlight Pair { get; }
        public IReadOnlyList<Node> Children => _children is { } children
            ? children
            : Array.Empty<Node>();

        public void AddChild(Node child)
        {
            _children ??= [];
            _children.Add(child);
        }
    }
}
