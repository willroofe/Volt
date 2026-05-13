namespace Volt;

public sealed class JsonLanguageService : ILanguageService
{
    public string Name => "JSON";
    public IReadOnlyList<string> Extensions { get; } = [".json", ".volt-workspace"];

    public LanguageSnapshot Analyze(string text, long sourceVersion)
    {
        var lexer = new JsonLexer(text);
        IReadOnlyList<JsonToken> tokens = lexer.Lex();

        var parser = new JsonParser(tokens, lexer.Diagnostics);
        SyntaxNode root = parser.ParseDocument();

        IReadOnlySet<TextRange> propertyRanges = parser.PropertyNameRanges;
        var publicTokens = new List<LanguageToken>();
        foreach (JsonToken token in tokens)
        {
            if (token.Kind == JsonTokenKind.EndOfFile)
                continue;

            LanguageTokenKind kind = token.Kind switch
            {
                JsonTokenKind.String when propertyRanges.Contains(token.Range) => LanguageTokenKind.PropertyName,
                JsonTokenKind.String => LanguageTokenKind.String,
                JsonTokenKind.Number => LanguageTokenKind.Number,
                JsonTokenKind.True or JsonTokenKind.False => LanguageTokenKind.Boolean,
                JsonTokenKind.Null => LanguageTokenKind.Null,
                JsonTokenKind.Invalid => LanguageTokenKind.Invalid,
                _ => LanguageTokenKind.Punctuation,
            };

            publicTokens.Add(new LanguageToken(token.Range, kind, GetScope(kind), token.Text));
        }

        return new LanguageSnapshot(Name, sourceVersion, root, publicTokens, parser.Diagnostics);
    }

    public LanguageDiagnosticsSnapshot AnalyzeDiagnostics(
        ILanguageTextSource source,
        long sourceVersion,
        IProgress<LanguageDiagnosticsProgress>? progress,
        CancellationToken cancellationToken) =>
        JsonDiagnosticsAnalyzer.Analyze(Name, source, sourceVersion, progress, cancellationToken);

    public LanguageRenderState GetRenderState(LanguageTextSegment segment, LanguageRenderState initialState)
    {
        bool inString = IsStringState(initialState);
        TextPosition stringStart = inString ? initialState.TokenStart : default;
        bool isEscaped = inString && initialState.IsEscaped;

        for (int i = 0; i < segment.Text.Length; i++)
        {
            char current = segment.Text[i];
            if (!inString)
            {
                if (current == '"')
                {
                    inString = true;
                    stringStart = new TextPosition(segment.Line, segment.StartColumn + i);
                    isEscaped = false;
                }

                continue;
            }

            if (isEscaped)
            {
                isEscaped = false;
            }
            else if (current == '\\')
            {
                isEscaped = true;
            }
            else if (current == '"' || current is '\r' or '\n')
            {
                inString = false;
                isEscaped = false;
            }
        }

        return inString
            ? CreateStringState(stringStart, isEscaped)
            : LanguageRenderState.Default;
    }

    public IReadOnlyList<LanguageToken> TokenizeForRendering(
        LanguageTextSegment segment,
        LanguageRenderState initialState)
    {
        if (segment.Text.Length == 0)
            return Array.Empty<LanguageToken>();

        var publicTokens = new List<LanguageToken>();
        int startIndex = 0;
        if (IsStringState(initialState))
        {
            startIndex = LexStringContinuation(segment, initialState, publicTokens);
            if (startIndex >= segment.Text.Length)
                return publicTokens;
        }

        var lexer = new JsonLexer(segment.Text[startIndex..], segment.Line, segment.StartColumn + startIndex);
        IReadOnlyList<JsonToken> tokens = lexer.Lex();
        for (int i = 0; i < tokens.Count; i++)
        {
            JsonToken token = tokens[i];
            if (token.Kind == JsonTokenKind.EndOfFile)
                continue;

            LanguageTokenKind kind = GetRenderTokenKind(token, segment);
            publicTokens.Add(new LanguageToken(token.Range, kind, GetScope(kind), token.Text));
        }

        return publicTokens;
    }

    private static int LexStringContinuation(
        LanguageTextSegment segment,
        LanguageRenderState initialState,
        List<LanguageToken> tokens)
    {
        bool isEscaped = initialState.IsEscaped;
        for (int i = 0; i < segment.Text.Length; i++)
        {
            char current = segment.Text[i];
            TextPosition end = new(segment.Line, segment.StartColumn + i + 1);

            if (isEscaped)
            {
                isEscaped = false;
                continue;
            }

            if (current == '\\')
            {
                isEscaped = true;
                continue;
            }

            if (current is '\r' or '\n')
            {
                tokens.Add(CreateToken(initialState, end, segment.Text[..(i + 1)]));
                return i + 1;
            }

            if (current == '"')
            {
                tokens.Add(CreateToken(initialState, end, segment.Text[..(i + 1)], segment));
                return i + 1;
            }
        }

        TextPosition segmentEnd = new(segment.Line, segment.StartColumn + segment.Text.Length);
        tokens.Add(CreateToken(initialState, segmentEnd, segment.Text));
        return segment.Text.Length;
    }

    private static LanguageToken CreateToken(
        LanguageRenderState state,
        TextPosition end,
        string text,
        LanguageTextSegment? segment = null)
    {
        LanguageTokenKind kind = segment.HasValue && IsPropertyNameForRendering(end, segment.Value)
            ? LanguageTokenKind.PropertyName
            : state.TokenKind;

        return new LanguageToken(
            new TextRange(state.TokenStart, end),
            kind,
            GetScope(kind),
            text);
    }

    private static LanguageTokenKind GetRenderTokenKind(JsonToken token, LanguageTextSegment segment)
    {
        return token.Kind switch
        {
            JsonTokenKind.String when IsPropertyNameForRendering(token.Range.End, segment) => LanguageTokenKind.PropertyName,
            JsonTokenKind.String => LanguageTokenKind.String,
            JsonTokenKind.Number => LanguageTokenKind.Number,
            JsonTokenKind.True or JsonTokenKind.False => LanguageTokenKind.Boolean,
            JsonTokenKind.Null => LanguageTokenKind.Null,
            JsonTokenKind.Invalid => LanguageTokenKind.Invalid,
            _ => LanguageTokenKind.Punctuation,
        };
    }

    private static bool IsPropertyNameForRendering(TextPosition tokenEnd, LanguageTextSegment segment)
    {
        if (tokenEnd.Line != segment.Line)
            return false;

        int offset = tokenEnd.Column - segment.StartColumn;
        if (offset < 0)
            return false;

        for (int i = offset; i < segment.Text.Length; i++)
        {
            char current = segment.Text[i];
            if (char.IsWhiteSpace(current))
                continue;

            return current == ':';
        }

        return false;
    }

    private static LanguageRenderState CreateStringState(TextPosition start, bool isEscaped = false) =>
        new(JsonSyntaxKinds.String, start, LanguageTokenKind.String, isEscaped);

    private static bool IsStringState(LanguageRenderState state) =>
        string.Equals(state.Mode, JsonSyntaxKinds.String, StringComparison.Ordinal);

    private static string GetScope(LanguageTokenKind kind) => kind switch
    {
        LanguageTokenKind.PropertyName => "property",
        LanguageTokenKind.String => "string",
        LanguageTokenKind.Number => "number",
        LanguageTokenKind.Boolean => "keyword",
        LanguageTokenKind.Null => "keyword",
        LanguageTokenKind.Invalid => "invalid",
        _ => "operator",
    };
}

internal static class JsonSyntaxKinds
{
    public const string Document = "json.document";
    public const string Object = "json.object";
    public const string Array = "json.array";
    public const string Property = "json.property";
    public const string String = "json.string";
    public const string Number = "json.number";
    public const string True = "json.true";
    public const string False = "json.false";
    public const string Null = "json.null";
    public const string Missing = "json.missing";
    public const string Error = "json.error";
}

internal enum JsonTokenKind
{
    LeftBrace,
    RightBrace,
    LeftBracket,
    RightBracket,
    Colon,
    Comma,
    String,
    Number,
    True,
    False,
    Null,
    Invalid,
    EndOfFile,
}

internal readonly record struct JsonToken(JsonTokenKind Kind, TextRange Range, string Text);

internal sealed class JsonLexer
{
    private readonly string _text;
    private readonly List<JsonToken> _tokens = [];
    private readonly List<ParseDiagnostic> _diagnostics = [];
    private int _index;
    private int _line;
    private int _column;

    public JsonLexer(string text, int startLine = 0, int startColumn = 0)
    {
        _text = text;
        _line = startLine;
        _column = startColumn;
    }

    public IReadOnlyList<ParseDiagnostic> Diagnostics => _diagnostics;

    public IReadOnlyList<JsonToken> Lex()
    {
        while (!IsAtEnd)
        {
            char current = Current;
            if (char.IsWhiteSpace(current))
            {
                Advance();
                continue;
            }

            TextPosition start = Position;
            switch (current)
            {
                case '{':
                    AddSingle(JsonTokenKind.LeftBrace);
                    break;
                case '}':
                    AddSingle(JsonTokenKind.RightBrace);
                    break;
                case '[':
                    AddSingle(JsonTokenKind.LeftBracket);
                    break;
                case ']':
                    AddSingle(JsonTokenKind.RightBracket);
                    break;
                case ':':
                    AddSingle(JsonTokenKind.Colon);
                    break;
                case ',':
                    AddSingle(JsonTokenKind.Comma);
                    break;
                case '"':
                    LexString();
                    break;
                case '-':
                    LexNumber();
                    break;
                default:
                    if (char.IsDigit(current))
                        LexNumber();
                    else if (char.IsLetter(current))
                        LexLiteralOrInvalid();
                    else
                        AddInvalid(start, Advance().ToString(), "Unexpected character.");
                    break;
            }
        }

        _tokens.Add(new JsonToken(JsonTokenKind.EndOfFile,
            new TextRange(Position, Position), ""));
        return _tokens;
    }

    private bool IsAtEnd => _index >= _text.Length;
    private char Current => IsAtEnd ? '\0' : _text[_index];
    private char Peek(int offset) => _index + offset < _text.Length ? _text[_index + offset] : '\0';
    private TextPosition Position => new(_line, _column);

    private void AddSingle(JsonTokenKind kind)
    {
        TextPosition start = Position;
        string text = Advance().ToString();
        _tokens.Add(new JsonToken(kind, new TextRange(start, Position), text));
    }

    private void LexLiteralOrInvalid()
    {
        TextPosition start = Position;
        string text = ReadWhile(char.IsLetter);
        JsonTokenKind kind = text switch
        {
            "true" => JsonTokenKind.True,
            "false" => JsonTokenKind.False,
            "null" => JsonTokenKind.Null,
            _ => JsonTokenKind.Invalid,
        };

        _tokens.Add(new JsonToken(kind, new TextRange(start, Position), text));
        if (kind == JsonTokenKind.Invalid)
            AddDiagnostic(new TextRange(start, Position), $"Unexpected literal '{text}'.");
    }

    private void LexString()
    {
        TextPosition start = Position;
        int startIndex = _index;
        Advance();

        while (!IsAtEnd)
        {
            char current = Current;
            if (current == '"')
            {
                Advance();
                AddToken(JsonTokenKind.String, start, startIndex);
                return;
            }

            if (current == '\r' || current == '\n')
            {
                AddDiagnostic(new TextRange(start, Position), "String literal is not terminated.");
                AddToken(JsonTokenKind.String, start, startIndex);
                return;
            }

            if (current == '\\')
            {
                Advance();
                if (IsAtEnd)
                    break;

                char escape = Current;
                if (escape == 'u')
                {
                    TextPosition escapeStart = Position;
                    Advance();
                    for (int i = 0; i < 4; i++)
                    {
                        if (!IsHexDigit(Current))
                        {
                            AddDiagnostic(new TextRange(escapeStart, Position), "Unicode escape must contain four hexadecimal digits.");
                            break;
                        }

                        Advance();
                    }
                    continue;
                }

                if (escape is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                {
                    Advance();
                    continue;
                }

                AddDiagnostic(new TextRange(Position, new TextPosition(_line, _column + 1)), $"Invalid escape sequence '\\{escape}'.");
                Advance();
                continue;
            }

            Advance();
        }

        AddDiagnostic(new TextRange(start, Position), "String literal is not terminated.");
        AddToken(JsonTokenKind.String, start, startIndex);
    }

    private void LexNumber()
    {
        TextPosition start = Position;
        int startIndex = _index;

        if (Current == '-')
            Advance();

        if (Current == '0')
        {
            Advance();
            if (char.IsDigit(Current))
                AddDiagnostic(new TextRange(start, Position), "JSON numbers cannot contain leading zeroes.");
        }
        else if (IsDigitOneToNine(Current))
        {
            ReadDigits();
        }
        else
        {
            AddDiagnostic(new TextRange(start, Position), "Expected digit after minus sign.");
        }

        if (Current == '.')
        {
            Advance();
            if (!char.IsDigit(Current))
                AddDiagnostic(new TextRange(start, Position), "Expected digit after decimal point.");
            ReadDigits();
        }

        if (Current is 'e' or 'E')
        {
            Advance();
            if (Current is '+' or '-')
                Advance();
            if (!char.IsDigit(Current))
                AddDiagnostic(new TextRange(start, Position), "Expected digit in exponent.");
            ReadDigits();
        }

        AddToken(JsonTokenKind.Number, start, startIndex);
    }

    private void ReadDigits()
    {
        while (char.IsDigit(Current))
            Advance();
    }

    private void AddToken(JsonTokenKind kind, TextPosition start, int startIndex)
    {
        string text = _text[startIndex.._index];
        _tokens.Add(new JsonToken(kind, new TextRange(start, Position), text));
    }

    private void AddInvalid(TextPosition start, string text, string message)
    {
        TextRange range = new(start, Position);
        _tokens.Add(new JsonToken(JsonTokenKind.Invalid, range, text));
        AddDiagnostic(range, message);
    }

    private void AddDiagnostic(TextRange range, string message) =>
        _diagnostics.Add(new ParseDiagnostic(range, DiagnosticSeverity.Error, message));

    private string ReadWhile(Func<char, bool> predicate)
    {
        int start = _index;
        while (!IsAtEnd && predicate(Current))
            Advance();
        return _text[start.._index];
    }

    private char Advance()
    {
        char current = Current;
        _index++;

        if (current == '\r')
        {
            if (!IsAtEnd && Current == '\n')
                _index++;
            _line++;
            _column = 0;
        }
        else if (current == '\n')
        {
            _line++;
            _column = 0;
        }
        else
        {
            _column++;
        }

        return current;
    }

    private static bool IsDigitOneToNine(char value) => value is >= '1' and <= '9';
    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

internal sealed class JsonParser
{
    private readonly IReadOnlyList<JsonToken> _tokens;
    private readonly List<ParseDiagnostic> _diagnostics;
    private readonly HashSet<TextRange> _propertyNameRanges = [];
    private int _index;

    public JsonParser(IReadOnlyList<JsonToken> tokens, IReadOnlyList<ParseDiagnostic> diagnostics)
    {
        _tokens = tokens;
        _diagnostics = [..diagnostics];
    }

    public IReadOnlyList<ParseDiagnostic> Diagnostics => _diagnostics;
    public IReadOnlySet<TextRange> PropertyNameRanges => _propertyNameRanges;

    public SyntaxNode ParseDocument()
    {
        TextPosition start = Current.Range.Start;
        SyntaxNode value = Current.Kind == JsonTokenKind.EndOfFile
            ? Missing("Expected JSON value.")
            : ParseValue();

        while (Current.Kind != JsonTokenKind.EndOfFile)
        {
            AddDiagnostic(Current.Range, "Only one top-level JSON value is allowed.");
            Advance();
        }

        TextRange range = new(start, Current.Range.End);
        return new SyntaxNode(JsonSyntaxKinds.Document, range, [value]);
    }

    private SyntaxNode ParseValue()
    {
        return Current.Kind switch
        {
            JsonTokenKind.LeftBrace => ParseObject(),
            JsonTokenKind.LeftBracket => ParseArray(),
            JsonTokenKind.String => Leaf(JsonSyntaxKinds.String, Advance()),
            JsonTokenKind.Number => Leaf(JsonSyntaxKinds.Number, Advance()),
            JsonTokenKind.True => Leaf(JsonSyntaxKinds.True, Advance()),
            JsonTokenKind.False => Leaf(JsonSyntaxKinds.False, Advance()),
            JsonTokenKind.Null => Leaf(JsonSyntaxKinds.Null, Advance()),
            JsonTokenKind.EndOfFile => Missing("Expected JSON value."),
            _ => ErrorNode("Expected JSON value."),
        };
    }

    private SyntaxNode ParseObject()
    {
        JsonToken open = Expect(JsonTokenKind.LeftBrace, "Expected '{'.");
        var properties = new List<SyntaxNode>();

        if (Match(JsonTokenKind.RightBrace, out JsonToken close))
            return new SyntaxNode(JsonSyntaxKinds.Object, Combine(open.Range, close.Range), properties);

        while (Current.Kind != JsonTokenKind.EndOfFile)
        {
            if (Current.Kind != JsonTokenKind.String)
            {
                properties.Add(ErrorNode("Expected property name."));
                if (!RecoverObject())
                    break;
            }
            else
            {
                JsonToken name = Advance();
                _propertyNameRanges.Add(name.Range);

                Expect(JsonTokenKind.Colon, "Expected ':' after property name.");
                SyntaxNode value = Current.Kind == JsonTokenKind.EndOfFile
                    ? Missing("Expected property value.")
                    : ParseValue();

                properties.Add(new SyntaxNode(JsonSyntaxKinds.Property,
                    Combine(name.Range, value.Range), [Leaf(JsonSyntaxKinds.String, name), value]));
            }

            if (Match(JsonTokenKind.Comma, out JsonToken comma))
            {
                if (Current.Kind == JsonTokenKind.RightBrace)
                    AddDiagnostic(comma.Range, "Trailing commas are not allowed in JSON objects.");
                else
                    continue;
            }

            if (Match(JsonTokenKind.RightBrace, out close))
                return new SyntaxNode(JsonSyntaxKinds.Object, Combine(open.Range, close.Range), properties);

            AddDiagnostic(Current.Range, "Expected ',' or '}' after object property.");
            if (!RecoverObject())
                break;
        }

        AddDiagnostic(open.Range, "Object is not terminated.");
        return new SyntaxNode(JsonSyntaxKinds.Object, Combine(open.Range, Previous.Range), properties);
    }

    private SyntaxNode ParseArray()
    {
        JsonToken open = Expect(JsonTokenKind.LeftBracket, "Expected '['.");
        var items = new List<SyntaxNode>();

        if (Match(JsonTokenKind.RightBracket, out JsonToken close))
            return new SyntaxNode(JsonSyntaxKinds.Array, Combine(open.Range, close.Range), items);

        while (Current.Kind != JsonTokenKind.EndOfFile)
        {
            items.Add(ParseValue());

            if (Match(JsonTokenKind.Comma, out JsonToken comma))
            {
                if (Current.Kind == JsonTokenKind.RightBracket)
                    AddDiagnostic(comma.Range, "Trailing commas are not allowed in JSON arrays.");
                else
                    continue;
            }

            if (Match(JsonTokenKind.RightBracket, out close))
                return new SyntaxNode(JsonSyntaxKinds.Array, Combine(open.Range, close.Range), items);

            AddDiagnostic(Current.Range, "Expected ',' or ']' after array item.");
            if (!RecoverArray())
                break;
        }

        AddDiagnostic(open.Range, "Array is not terminated.");
        return new SyntaxNode(JsonSyntaxKinds.Array, Combine(open.Range, Previous.Range), items);
    }

    private bool RecoverObject()
    {
        while (Current.Kind != JsonTokenKind.EndOfFile)
        {
            if (Current.Kind is JsonTokenKind.Comma or JsonTokenKind.RightBrace)
                return true;
            Advance();
        }

        return false;
    }

    private bool RecoverArray()
    {
        while (Current.Kind != JsonTokenKind.EndOfFile)
        {
            if (Current.Kind is JsonTokenKind.Comma or JsonTokenKind.RightBracket)
                return true;
            Advance();
        }

        return false;
    }

    private SyntaxNode Missing(string message)
    {
        AddDiagnostic(Current.Range, message);
        return new SyntaxNode(JsonSyntaxKinds.Missing, new TextRange(Current.Range.Start, Current.Range.Start), []);
    }

    private SyntaxNode ErrorNode(string message)
    {
        AddDiagnostic(Current.Range, message);
        JsonToken token = Advance();
        return new SyntaxNode(JsonSyntaxKinds.Error, token.Range, [], token.Text);
    }

    private SyntaxNode Leaf(string kind, JsonToken token) =>
        new(kind, token.Range, [], token.Text);

    private bool Match(JsonTokenKind kind, out JsonToken token)
    {
        if (Current.Kind == kind)
        {
            token = Advance();
            return true;
        }

        token = default;
        return false;
    }

    private JsonToken Expect(JsonTokenKind kind, string message)
    {
        if (Current.Kind == kind)
            return Advance();

        AddDiagnostic(Current.Range, message);
        return new JsonToken(kind, new TextRange(Current.Range.Start, Current.Range.Start), "");
    }

    private JsonToken Advance()
    {
        JsonToken token = Current;
        if (Current.Kind != JsonTokenKind.EndOfFile)
            _index++;
        return token;
    }

    private JsonToken Current => _tokens[Math.Min(_index, _tokens.Count - 1)];
    private JsonToken Previous => _tokens[Math.Max(0, Math.Min(_index - 1, _tokens.Count - 1))];

    private void AddDiagnostic(TextRange range, string message) =>
        _diagnostics.Add(new ParseDiagnostic(range, DiagnosticSeverity.Error, message));

    private static TextRange Combine(TextRange start, TextRange end) =>
        new(start.Start, end.End);
}

internal static class JsonDiagnosticsAnalyzer
{
    private const int SegmentSize = 64 * 1024;
    private const int MaxDiagnostics = 1000;
    private const long ProgressReportInterval = 1 * 1024 * 1024;

    public static LanguageDiagnosticsSnapshot Analyze(
        string languageName,
        ILanguageTextSource source,
        long sourceVersion,
        IProgress<LanguageDiagnosticsProgress>? progress,
        CancellationToken cancellationToken)
    {
        var analyzer = new Analyzer(source, progress, cancellationToken);
        return new LanguageDiagnosticsSnapshot(
            languageName,
            sourceVersion,
            analyzer.Analyze(),
            IsComplete: true,
            new LanguageDiagnosticsProgress(source.CharCountWithoutLineEndings, source.CharCountWithoutLineEndings),
            analyzer.HasMoreDiagnostics);
    }

    private enum ContainerKind
    {
        Object,
        Array,
    }

    private enum ContainerState
    {
        ObjectPropertyOrEnd,
        ObjectColon,
        ObjectValue,
        ObjectCommaOrEnd,
        ArrayValueOrEnd,
        ArrayCommaOrEnd,
    }

    private enum ScalarKind
    {
        String,
        Number,
        Literal,
        Invalid,
    }

    private readonly record struct Frame(
        ContainerKind Kind,
        ContainerState State,
        TextPosition Start,
        bool AfterComma);

    private sealed class Analyzer
    {
        private readonly SourceCursor _cursor;
        private readonly IProgress<LanguageDiagnosticsProgress>? _progress;
        private readonly CancellationToken _cancellationToken;
        private readonly List<ParseDiagnostic> _diagnostics = [];
        private readonly List<Frame> _stack = [];
        private bool _rootHasValue;
        private bool _hasMoreDiagnostics;
        private long _nextProgressReport = ProgressReportInterval;

        public Analyzer(
            ILanguageTextSource source,
            IProgress<LanguageDiagnosticsProgress>? progress,
            CancellationToken cancellationToken)
        {
            _cursor = new SourceCursor(source);
            _progress = progress;
            _cancellationToken = cancellationToken;
        }

        public bool HasMoreDiagnostics => _hasMoreDiagnostics;

        public IReadOnlyList<ParseDiagnostic> Analyze()
        {
            ReportProgress(force: true);
            while (!_cursor.IsAtEnd)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(force: false);

                if (_cursor.IsAtLineEnd)
                {
                    _cursor.AdvanceLine();
                    continue;
                }

                char current = _cursor.Current;
                if (char.IsWhiteSpace(current))
                {
                    _cursor.Advance();
                    continue;
                }

                TextPosition start = _cursor.Position;
                switch (current)
                {
                    case '{':
                        _cursor.Advance();
                        StartContainer(ContainerKind.Object, start);
                        break;
                    case '}':
                        _cursor.Advance();
                        CloseObject(start, _cursor.Position);
                        break;
                    case '[':
                        _cursor.Advance();
                        StartContainer(ContainerKind.Array, start);
                        break;
                    case ']':
                        _cursor.Advance();
                        CloseArray(start, _cursor.Position);
                        break;
                    case ':':
                        _cursor.Advance();
                        HandleColon(start, _cursor.Position);
                        break;
                    case ',':
                        _cursor.Advance();
                        HandleComma(start, _cursor.Position);
                        break;
                    case '"':
                        LexString();
                        break;
                    case '-':
                        LexNumber();
                        break;
                    default:
                        if (char.IsDigit(current))
                            LexNumber();
                        else if (char.IsLetter(current))
                            LexLiteral();
                        else
                            LexUnexpectedCharacter();
                        break;
                }
            }

            TextPosition end = _cursor.Position;
            if (!_rootHasValue && _stack.Count == 0)
                AddDiagnostic(new TextRange(end, end), "Expected JSON value.");

            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                Frame frame = _stack[i];
                AddDiagnostic(new TextRange(frame.Start, new TextPosition(frame.Start.Line, frame.Start.Column + 1)),
                    frame.Kind == ContainerKind.Object
                        ? "Object is not terminated."
                        : "Array is not terminated.");
            }

            ReportProgress(force: true);
            return _diagnostics;
        }

        private void StartContainer(ContainerKind kind, TextPosition start)
        {
            if (!CanStartValue(start))
                return;

            _stack.Add(new Frame(
                kind,
                kind == ContainerKind.Object
                    ? ContainerState.ObjectPropertyOrEnd
                    : ContainerState.ArrayValueOrEnd,
                start,
                AfterComma: false));
        }

        private void CloseObject(TextPosition start, TextPosition end)
        {
            if (_stack.Count == 0 || CurrentFrame.Kind != ContainerKind.Object)
            {
                AddDiagnostic(new TextRange(start, end), "Unexpected '}'.");
                return;
            }

            Frame frame = CurrentFrame;
            if (frame.State == ContainerState.ObjectColon)
                AddDiagnostic(new TextRange(start, end), "Expected ':' after property name.");
            else if (frame.State == ContainerState.ObjectValue)
                AddDiagnostic(new TextRange(start, end), "Expected property value.");
            else if (frame.State == ContainerState.ObjectPropertyOrEnd && frame.AfterComma)
                AddDiagnostic(new TextRange(start, end), "Trailing commas are not allowed in JSON objects.");
            else if (frame.State != ContainerState.ObjectPropertyOrEnd
                     && frame.State != ContainerState.ObjectCommaOrEnd)
                AddDiagnostic(new TextRange(start, end), "Expected ',' or '}' after object property.");

            _stack.RemoveAt(_stack.Count - 1);
            CompleteValue(start, end);
        }

        private void CloseArray(TextPosition start, TextPosition end)
        {
            if (_stack.Count == 0 || CurrentFrame.Kind != ContainerKind.Array)
            {
                AddDiagnostic(new TextRange(start, end), "Unexpected ']'.");
                return;
            }

            Frame frame = CurrentFrame;
            if (frame.State == ContainerState.ArrayValueOrEnd && frame.AfterComma)
                AddDiagnostic(new TextRange(start, end), "Trailing commas are not allowed in JSON arrays.");
            else if (frame.State != ContainerState.ArrayValueOrEnd
                     && frame.State != ContainerState.ArrayCommaOrEnd)
                AddDiagnostic(new TextRange(start, end), "Expected ',' or ']' after array item.");

            _stack.RemoveAt(_stack.Count - 1);
            CompleteValue(start, end);
        }

        private void HandleColon(TextPosition start, TextPosition end)
        {
            if (_stack.Count > 0
                && CurrentFrame.Kind == ContainerKind.Object
                && CurrentFrame.State == ContainerState.ObjectColon)
            {
                CurrentFrame = CurrentFrame with
                {
                    State = ContainerState.ObjectValue,
                    AfterComma = false
                };
                return;
            }

            AddDiagnostic(new TextRange(start, end), "Unexpected ':'.");
        }

        private void HandleComma(TextPosition start, TextPosition end)
        {
            if (_stack.Count == 0)
            {
                AddDiagnostic(new TextRange(start, end), "Unexpected ','.");
                return;
            }

            Frame frame = CurrentFrame;
            if (frame.Kind == ContainerKind.Object && frame.State == ContainerState.ObjectCommaOrEnd)
            {
                CurrentFrame = frame with
                {
                    State = ContainerState.ObjectPropertyOrEnd,
                    AfterComma = true
                };
                return;
            }

            if (frame.Kind == ContainerKind.Array && frame.State == ContainerState.ArrayCommaOrEnd)
            {
                CurrentFrame = frame with
                {
                    State = ContainerState.ArrayValueOrEnd,
                    AfterComma = true
                };
                return;
            }

            AddDiagnostic(new TextRange(start, end),
                frame.Kind == ContainerKind.Object
                    ? "Expected object property before ','."
                    : "Expected array item before ','.");
        }

        private void LexString()
        {
            TextPosition start = _cursor.Position;
            _cursor.Advance();

            while (!_cursor.IsAtEnd)
            {
                if (_cursor.IsAtLineEnd)
                {
                    AddDiagnostic(new TextRange(start, _cursor.Position), "String literal is not terminated.");
                    HandleScalar(ScalarKind.String, start, _cursor.Position);
                    return;
                }

                char current = _cursor.Current;
                if (current == '"')
                {
                    _cursor.Advance();
                    HandleScalar(ScalarKind.String, start, _cursor.Position);
                    return;
                }

                if (current == '\\')
                {
                    _cursor.Advance();
                    if (_cursor.IsAtEnd || _cursor.IsAtLineEnd)
                    {
                        AddDiagnostic(new TextRange(start, _cursor.Position), "String literal is not terminated.");
                        HandleScalar(ScalarKind.String, start, _cursor.Position);
                        return;
                    }

                    TextPosition escapeStart = _cursor.Position;
                    char escape = _cursor.Current;
                    if (escape == 'u')
                    {
                        _cursor.Advance();
                        bool valid = true;
                        for (int i = 0; i < 4; i++)
                        {
                            if (_cursor.IsAtEnd || _cursor.IsAtLineEnd || !IsHexDigit(_cursor.Current))
                            {
                                valid = false;
                                break;
                            }

                            _cursor.Advance();
                        }

                        if (!valid)
                            AddDiagnostic(new TextRange(escapeStart, _cursor.Position),
                                "Unicode escape must contain four hexadecimal digits.");
                        continue;
                    }

                    if (escape is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                    {
                        _cursor.Advance();
                        continue;
                    }

                    _cursor.Advance();
                    AddDiagnostic(new TextRange(escapeStart, _cursor.Position),
                        $"Invalid escape sequence '\\{escape}'.");
                    continue;
                }

                _cursor.Advance();
            }

            AddDiagnostic(new TextRange(start, _cursor.Position), "String literal is not terminated.");
            HandleScalar(ScalarKind.String, start, _cursor.Position);
        }

        private void LexNumber()
        {
            TextPosition start = _cursor.Position;
            if (_cursor.Current == '-')
                _cursor.Advance();

            if (_cursor.IsAtEnd || _cursor.IsAtLineEnd || !char.IsDigit(_cursor.Current))
            {
                AddDiagnostic(new TextRange(start, _cursor.Position), "Expected digit after minus sign.");
                HandleScalar(ScalarKind.Number, start, _cursor.Position);
                return;
            }

            if (_cursor.Current == '0')
            {
                _cursor.Advance();
                if (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && char.IsDigit(_cursor.Current))
                    AddDiagnostic(new TextRange(start, _cursor.Position), "JSON numbers cannot contain leading zeroes.");
            }
            else
            {
                ReadDigits();
            }

            if (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && _cursor.Current == '.')
            {
                _cursor.Advance();
                if (_cursor.IsAtEnd || _cursor.IsAtLineEnd || !char.IsDigit(_cursor.Current))
                    AddDiagnostic(new TextRange(start, _cursor.Position), "Expected digit after decimal point.");
                ReadDigits();
            }

            if (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && _cursor.Current is 'e' or 'E')
            {
                _cursor.Advance();
                if (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && _cursor.Current is '+' or '-')
                    _cursor.Advance();

                if (_cursor.IsAtEnd || _cursor.IsAtLineEnd || !char.IsDigit(_cursor.Current))
                    AddDiagnostic(new TextRange(start, _cursor.Position), "Expected digit in exponent.");
                ReadDigits();
            }

            HandleScalar(ScalarKind.Number, start, _cursor.Position);
        }

        private void LexLiteral()
        {
            TextPosition start = _cursor.Position;
            string text = ReadLetters();
            ScalarKind kind = text is "true" or "false" or "null"
                ? ScalarKind.Literal
                : ScalarKind.Invalid;
            if (kind == ScalarKind.Invalid)
                AddDiagnostic(new TextRange(start, _cursor.Position), $"Unexpected literal '{text}'.");

            HandleScalar(kind, start, _cursor.Position);
        }

        private void LexUnexpectedCharacter()
        {
            TextPosition start = _cursor.Position;
            char current = _cursor.Current;
            _cursor.Advance();
            AddDiagnostic(new TextRange(start, _cursor.Position), $"Unexpected character '{current}'.");
        }

        private void HandleScalar(ScalarKind kind, TextPosition start, TextPosition end)
        {
            if (_stack.Count == 0)
            {
                if (_rootHasValue)
                    AddDiagnostic(new TextRange(start, end), "Only one top-level JSON value is allowed.");
                else
                    _rootHasValue = true;
                return;
            }

            Frame frame = CurrentFrame;
            if (frame.Kind == ContainerKind.Object)
            {
                switch (frame.State)
                {
                    case ContainerState.ObjectPropertyOrEnd:
                        if (kind == ScalarKind.String)
                        {
                            CurrentFrame = frame with
                            {
                                State = ContainerState.ObjectColon,
                                AfterComma = false
                            };
                        }
                        else
                        {
                            AddDiagnostic(new TextRange(start, end), "Expected property name.");
                        }
                        return;

                    case ContainerState.ObjectColon:
                        AddDiagnostic(new TextRange(start, end), "Expected ':' after property name.");
                        CurrentFrame = frame with
                        {
                            State = ContainerState.ObjectCommaOrEnd,
                            AfterComma = false
                        };
                        return;

                    case ContainerState.ObjectValue:
                        CurrentFrame = frame with
                        {
                            State = ContainerState.ObjectCommaOrEnd,
                            AfterComma = false
                        };
                        return;

                    case ContainerState.ObjectCommaOrEnd:
                        AddDiagnostic(new TextRange(start, end), "Expected ',' or '}' after object property.");
                        return;
                }
            }

            if (frame.Kind == ContainerKind.Array)
            {
                if (frame.State == ContainerState.ArrayValueOrEnd)
                {
                    CurrentFrame = frame with
                    {
                        State = ContainerState.ArrayCommaOrEnd,
                        AfterComma = false
                    };
                }
                else
                {
                    AddDiagnostic(new TextRange(start, end), "Expected ',' or ']' after array item.");
                }
            }
        }

        private bool CanStartValue(TextPosition start)
        {
            if (_stack.Count == 0)
            {
                if (_rootHasValue)
                {
                    AddDiagnostic(new TextRange(start, new TextPosition(start.Line, start.Column + 1)),
                        "Only one top-level JSON value is allowed.");
                    return false;
                }

                return true;
            }

            Frame frame = CurrentFrame;
            if (frame.Kind == ContainerKind.Array && frame.State == ContainerState.ArrayValueOrEnd)
                return true;
            if (frame.Kind == ContainerKind.Object && frame.State == ContainerState.ObjectValue)
                return true;

            AddDiagnostic(new TextRange(start, new TextPosition(start.Line, start.Column + 1)),
                frame.Kind == ContainerKind.Object
                    ? ExpectedObjectMessage(frame.State)
                    : "Expected ',' or ']' after array item.");
            return false;
        }

        private void CompleteValue(TextPosition start, TextPosition end)
        {
            if (_stack.Count == 0)
            {
                if (_rootHasValue)
                    AddDiagnostic(new TextRange(start, end), "Only one top-level JSON value is allowed.");
                else
                    _rootHasValue = true;
                return;
            }

            Frame frame = CurrentFrame;
            if (frame.Kind == ContainerKind.Object && frame.State == ContainerState.ObjectValue)
            {
                CurrentFrame = frame with
                {
                    State = ContainerState.ObjectCommaOrEnd,
                    AfterComma = false
                };
                return;
            }

            if (frame.Kind == ContainerKind.Array && frame.State == ContainerState.ArrayValueOrEnd)
            {
                CurrentFrame = frame with
                {
                    State = ContainerState.ArrayCommaOrEnd,
                    AfterComma = false
                };
            }
        }

        private Frame CurrentFrame
        {
            get => _stack[^1];
            set => _stack[^1] = value;
        }

        private void ReadDigits()
        {
            while (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && char.IsDigit(_cursor.Current))
                _cursor.Advance();
        }

        private string ReadLetters()
        {
            TextPosition start = _cursor.Position;
            while (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && char.IsLetter(_cursor.Current))
                _cursor.Advance();

            int length = _cursor.Position.Column - start.Column;
            return _cursor.GetText(start.Line, start.Column, length);
        }

        private void AddDiagnostic(TextRange range, string message)
        {
            if (_diagnostics.Count < MaxDiagnostics)
            {
                _diagnostics.Add(new ParseDiagnostic(range, DiagnosticSeverity.Error, message));
            }
            else
            {
                _hasMoreDiagnostics = true;
            }
        }

        private void ReportProgress(bool force)
        {
            if (_progress == null)
                return;

            if (!force && _cursor.CharactersRead < _nextProgressReport)
                return;

            _nextProgressReport = _cursor.CharactersRead + ProgressReportInterval;
            _progress.Report(new LanguageDiagnosticsProgress(
                _cursor.CharactersRead,
                _cursor.TotalCharacters));
        }

        private static string ExpectedObjectMessage(ContainerState state) => state switch
        {
            ContainerState.ObjectPropertyOrEnd => "Expected property name.",
            ContainerState.ObjectColon => "Expected ':' after property name.",
            ContainerState.ObjectValue => "Expected property value.",
            _ => "Expected ',' or '}' after object property.",
        };
    }

    private sealed class SourceCursor
    {
        private readonly ILanguageTextSource _source;
        private string _segment = "";
        private int _segmentStartColumn = -1;
        private int _segmentIndex;
        private int _lineLength = -1;

        public SourceCursor(ILanguageTextSource source)
        {
            _source = source;
            TotalCharacters = source.CharCountWithoutLineEndings;
        }

        public int Line { get; private set; }
        public int Column { get; private set; }
        public long CharactersRead { get; private set; }
        public long TotalCharacters { get; }
        public bool IsAtEnd => Line >= _source.LineCount;
        public TextPosition Position => new(Line, Column);

        public bool IsAtLineEnd
        {
            get
            {
                if (IsAtEnd)
                    return true;
                return Column >= CurrentLineLength;
            }
        }

        public char Current
        {
            get
            {
                EnsureSegment();
                return _segment[_segmentIndex];
            }
        }

        public void Advance()
        {
            if (IsAtLineEnd)
                return;

            EnsureSegment();
            Column++;
            _segmentIndex++;
            CharactersRead++;
        }

        public void AdvanceLine()
        {
            if (IsAtEnd)
                return;

            int unread = Math.Max(0, CurrentLineLength - Column);
            CharactersRead += unread;
            Line++;
            Column = 0;
            _lineLength = -1;
            _segment = "";
            _segmentStartColumn = -1;
            _segmentIndex = 0;
        }

        public string GetText(int line, int startColumn, int length) =>
            length <= 0 ? "" : _source.GetLineSegment(line, startColumn, length);

        private int CurrentLineLength
        {
            get
            {
                if (_lineLength < 0)
                    _lineLength = _source.GetLineLength(Line);
                return _lineLength;
            }
        }

        private void EnsureSegment()
        {
            if (IsAtEnd || IsAtLineEnd)
                return;

            if (_segment.Length > 0
                && Column >= _segmentStartColumn
                && Column < _segmentStartColumn + _segment.Length)
            {
                _segmentIndex = Column - _segmentStartColumn;
                return;
            }

            int count = Math.Min(SegmentSize, CurrentLineLength - Column);
            _segment = _source.GetLineSegment(Line, Column, count);
            _segmentStartColumn = Column;
            _segmentIndex = 0;
        }
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
