namespace Volt;

public readonly record struct TextPosition(int Line, int Column);

public readonly record struct TextRange(TextPosition Start, TextPosition End)
{
    public static TextRange FromBounds(int startLine, int startColumn, int endLine, int endColumn) =>
        new(new TextPosition(startLine, startColumn), new TextPosition(endLine, endColumn));
}

public enum LanguageTokenKind
{
    Punctuation,
    PropertyName,
    String,
    Number,
    Boolean,
    Null,
    Invalid,
}

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Information,
}

public sealed record LanguageToken(TextRange Range, LanguageTokenKind Kind, string Scope, string Text);

public sealed record ParseDiagnostic(TextRange Range, DiagnosticSeverity Severity, string Message);

public enum LanguagePairKind
{
    Object,
    Array,
    String,
}

public sealed record LanguagePairHighlight(
    LanguagePairKind Kind,
    TextRange OpenRange,
    TextRange CloseRange);

public sealed class LanguagePairIndex
{
    public static LanguagePairIndex Empty { get; } = new(Array.Empty<LanguagePairHighlight>());

    public LanguagePairIndex(IReadOnlyList<LanguagePairHighlight> pairs)
    {
        Pairs = pairs;
        _roots = BuildTree(pairs);
    }

    private readonly IReadOnlyList<Node> _roots;

    public IReadOnlyList<LanguagePairHighlight> Pairs { get; }

    public IReadOnlyList<LanguagePairHighlight> GetMatchingPairs(TextPosition caret)
    {
        if (_roots.Count == 0)
            return Array.Empty<LanguagePairHighlight>();

        var matches = new List<LanguagePairHighlight>();
        AddMatchingPath(_roots, caret, matches);
        if (matches.Count == 0)
            return Array.Empty<LanguagePairHighlight>();

        return matches;
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
            if (stack.Count == 0)
                roots.Add(node);
            else
                stack.Peek().AddChild(node);

            stack.Push(node);
        }

        Freeze(roots);
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

    private static void Freeze(List<Node> nodes)
    {
        foreach (Node node in nodes)
        {
            if (node.MutableChildren != null)
                Freeze(node.MutableChildren);
        }

        foreach (Node node in nodes)
            node.Children = node.MutableChildren?.ToArray() ?? Array.Empty<Node>();
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
        private List<Node>? _mutableChildren;

        public Node(LanguagePairHighlight pair)
        {
            Pair = pair;
        }

        public LanguagePairHighlight Pair { get; }
        public List<Node>? MutableChildren => _mutableChildren;
        public IReadOnlyList<Node> Children { get; set; } = Array.Empty<Node>();

        public void AddChild(Node child)
        {
            _mutableChildren ??= [];
            _mutableChildren.Add(child);
        }
    }
}

public sealed record SyntaxNode(string Kind, TextRange Range, IReadOnlyList<SyntaxNode> Children, string? Text = null);

public sealed record LanguageSnapshot(
    string LanguageName,
    long SourceVersion,
    SyntaxNode Root,
    IReadOnlyList<LanguageToken> Tokens,
    IReadOnlyList<ParseDiagnostic> Diagnostics);

public readonly record struct LanguageTextSegment(int Line, int StartColumn, string Text);

public interface ILanguageTextSource
{
    int LineCount { get; }
    long CharCountWithoutLineEndings { get; }
    int GetLineLength(int line);
    string GetLineSegment(int line, int startColumn, int length);
}

internal readonly record struct LanguageTextReadSegment(
    int Line,
    int StartColumn,
    string Text,
    bool EndsAtLineEnd,
    bool IsEnd);

internal interface ILanguageTextStream : IDisposable
{
    LanguageTextReadSegment ReadSegment(int maxLength, CancellationToken cancellationToken);
}

internal interface ILanguageTextStreamSource
{
    bool TryCreateTextStream(int startLine, int lineCount, out ILanguageTextStream stream);
}

public sealed record LanguageDiagnosticsProgress(long CharactersProcessed, long TotalCharacters)
{
    public int? Percent =>
        TotalCharacters <= 0
            ? null
            : (int)Math.Clamp(CharactersProcessed * 100 / TotalCharacters, 0, 100);
}

public sealed record LanguageDiagnosticsSnapshot(
    string LanguageName,
    long SourceVersion,
    IReadOnlyList<ParseDiagnostic> Diagnostics,
    bool IsComplete,
    LanguageDiagnosticsProgress? Progress,
    bool HasMoreDiagnostics);

public readonly record struct LanguageRenderState(
    string Mode,
    TextPosition TokenStart,
    LanguageTokenKind TokenKind,
    bool IsEscaped)
{
    public static LanguageRenderState Default { get; } =
        new("", new TextPosition(0, 0), LanguageTokenKind.Punctuation, false);

    public bool IsDefault => string.IsNullOrEmpty(Mode);
}

public interface ILanguageService
{
    string Name { get; }
    IReadOnlyList<string> Extensions { get; }
    LanguageSnapshot Analyze(string text, long sourceVersion);
    LanguageDiagnosticsSnapshot AnalyzeDiagnostics(
        ILanguageTextSource source,
        long sourceVersion,
        IProgress<LanguageDiagnosticsProgress>? progress,
        CancellationToken cancellationToken);
    LanguageRenderState GetRenderState(LanguageTextSegment segment, LanguageRenderState initialState);
    IReadOnlyList<LanguageToken> TokenizeForRendering(LanguageTextSegment segment, LanguageRenderState initialState);
    IReadOnlyList<LanguagePairHighlight> GetMatchingPairs(
        LanguageSnapshot snapshot,
        ILanguageTextSource source,
        TextPosition caret) =>
        Array.Empty<LanguagePairHighlight>();
    LanguagePairIndex? CreateMatchingPairIndex(
        ILanguageTextSource source,
        CancellationToken cancellationToken) =>
        null;
    IReadOnlyList<LanguagePairHighlight> GetMatchingPairs(
        ILanguageTextSource source,
        TextPosition caret,
        CancellationToken cancellationToken) =>
        CreateMatchingPairIndex(source, cancellationToken)?.GetMatchingPairs(caret)
        ?? Array.Empty<LanguagePairHighlight>();
}
