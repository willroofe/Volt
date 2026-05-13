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

public sealed record SyntaxNode(string Kind, TextRange Range, IReadOnlyList<SyntaxNode> Children, string? Text = null);

public sealed record LanguageSnapshot(
    string LanguageName,
    long SourceVersion,
    SyntaxNode Root,
    IReadOnlyList<LanguageToken> Tokens,
    IReadOnlyList<ParseDiagnostic> Diagnostics);

public readonly record struct LanguageTextSegment(int Line, int StartColumn, string Text);

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
    LanguageRenderState GetRenderState(LanguageTextSegment segment, LanguageRenderState initialState);
    IReadOnlyList<LanguageToken> TokenizeForRendering(LanguageTextSegment segment, LanguageRenderState initialState);
}
