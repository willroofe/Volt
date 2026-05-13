using System.Diagnostics;
using System.Text;

namespace Volt;

public sealed class JsonLanguageService : ILanguageService
{
    public string Name => "JSON";
    public IReadOnlyList<string> Extensions { get; } = [".json", ".volt-workspace"];

    public LanguageSnapshot Analyze(string text, long sourceVersion)
    {
        var lexer = new JsonLexer(text);
        IReadOnlyList<JsonToken> tokens = lexer.Lex();

        IReadOnlyList<ParseDiagnostic> diagnostics = AnalyzeTokenDiagnostics(tokens, lexer.Diagnostics);

        var parser = new JsonParser(tokens);
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
                JsonTokenKind.InvalidLiteral or JsonTokenKind.InvalidCharacter => LanguageTokenKind.Invalid,
                _ => LanguageTokenKind.Punctuation,
            };

            publicTokens.Add(new LanguageToken(token.Range, kind, GetScope(kind), token.Text));
        }

        return new LanguageSnapshot(Name, sourceVersion, root, publicTokens, diagnostics);
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
            JsonTokenKind.InvalidLiteral or JsonTokenKind.InvalidCharacter => LanguageTokenKind.Invalid,
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

    private static IReadOnlyList<ParseDiagnostic> AnalyzeTokenDiagnostics(
        IReadOnlyList<JsonToken> tokens,
        IReadOnlyList<ParseDiagnostic> lexerDiagnostics)
    {
        var diagnostics = new JsonDiagnosticSink();
        diagnostics.AddRange(lexerDiagnostics);

        var validator = new JsonGrammarValidator(diagnostics);
        foreach (JsonToken token in tokens)
            validator.Accept(token);

        return diagnostics.Diagnostics;
    }
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
    InvalidLiteral,
    InvalidCharacter,
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
                    {
                        char invalid = Advance();
                        AddInvalid(start, invalid.ToString(), $"Unexpected character '{invalid}'.");
                    }
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
            _ => JsonTokenKind.InvalidLiteral,
        };

        _tokens.Add(new JsonToken(kind, new TextRange(start, Position), text));
        if (kind == JsonTokenKind.InvalidLiteral)
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
        _tokens.Add(new JsonToken(JsonTokenKind.InvalidCharacter, range, text));
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

    private static bool IsDigit(char value) =>
        value is >= '0' and <= '9';

    private static bool IsAsciiLetter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsJsonWhitespace(char value) =>
        value is ' ' or '\t' or '\r' or '\n';
}

internal sealed class JsonDiagnosticSink
{
    private readonly int _maxDiagnostics;
    private readonly List<ParseDiagnostic> _diagnostics = [];

    public JsonDiagnosticSink(int maxDiagnostics = int.MaxValue)
    {
        _maxDiagnostics = maxDiagnostics;
    }

    public IReadOnlyList<ParseDiagnostic> Diagnostics => _diagnostics;
    public bool HasMoreDiagnostics { get; private set; }

    public void Add(TextRange range, string message) =>
        Add(new ParseDiagnostic(range, DiagnosticSeverity.Error, message));

    public void Add(ParseDiagnostic diagnostic)
    {
        if (_diagnostics.Count < _maxDiagnostics)
        {
            _diagnostics.Add(diagnostic);
        }
        else
        {
            HasMoreDiagnostics = true;
        }
    }

    public void AddRange(IEnumerable<ParseDiagnostic> diagnostics)
    {
        foreach (ParseDiagnostic diagnostic in diagnostics)
            Add(diagnostic);
    }
}

internal sealed class JsonGrammarValidator
{
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

    private readonly JsonDiagnosticSink _diagnostics;
    private readonly List<Frame> _stack = [];
    private bool _rootHasValue;
    private bool _finished;

    public JsonGrammarValidator(JsonDiagnosticSink diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public void Accept(JsonToken token) =>
        Accept(token.Kind, token.Range);

    public void Accept(JsonTokenKind kind, TextRange range)
    {
        switch (kind)
        {
            case JsonTokenKind.LeftBrace:
                StartContainer(ContainerKind.Object, range);
                break;
            case JsonTokenKind.RightBrace:
                CloseObject(range);
                break;
            case JsonTokenKind.LeftBracket:
                StartContainer(ContainerKind.Array, range);
                break;
            case JsonTokenKind.RightBracket:
                CloseArray(range);
                break;
            case JsonTokenKind.Colon:
                HandleColon(range);
                break;
            case JsonTokenKind.Comma:
                HandleComma(range);
                break;
            case JsonTokenKind.String:
                HandleScalar(ScalarKind.String, range);
                break;
            case JsonTokenKind.Number:
                HandleScalar(ScalarKind.Number, range);
                break;
            case JsonTokenKind.True:
            case JsonTokenKind.False:
            case JsonTokenKind.Null:
                HandleScalar(ScalarKind.Literal, range);
                break;
            case JsonTokenKind.InvalidLiteral:
                HandleScalar(ScalarKind.Invalid, range);
                break;
            case JsonTokenKind.InvalidCharacter:
                break;
            case JsonTokenKind.EndOfFile:
                Finish(range.Start);
                break;
        }
    }

    public void Finish(TextPosition end)
    {
        if (_finished)
            return;

        _finished = true;
        if (!_rootHasValue && _stack.Count == 0)
            _diagnostics.Add(new TextRange(end, end), "Expected JSON value.");

        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            Frame frame = _stack[i];
            _diagnostics.Add(new TextRange(frame.Start, new TextPosition(frame.Start.Line, frame.Start.Column + 1)),
                frame.Kind == ContainerKind.Object
                    ? "Object is not terminated."
                    : "Array is not terminated.");
        }
    }

    private void StartContainer(ContainerKind kind, TextRange range)
    {
        if (!CanStartValue(range.Start))
            return;

        _stack.Add(new Frame(
            kind,
            kind == ContainerKind.Object
                ? ContainerState.ObjectPropertyOrEnd
                : ContainerState.ArrayValueOrEnd,
            range.Start,
            AfterComma: false));
    }

    private void CloseObject(TextRange range)
    {
        if (_stack.Count == 0 || CurrentFrame.Kind != ContainerKind.Object)
        {
            _diagnostics.Add(range, "Unexpected '}'.");
            return;
        }

        Frame frame = CurrentFrame;
        if (frame.State == ContainerState.ObjectColon)
            _diagnostics.Add(range, "Expected ':' after property name.");
        else if (frame.State == ContainerState.ObjectValue)
            _diagnostics.Add(range, "Expected property value.");
        else if (frame.State == ContainerState.ObjectPropertyOrEnd && frame.AfterComma)
            _diagnostics.Add(range, "Trailing commas are not allowed in JSON objects.");
        else if (frame.State != ContainerState.ObjectPropertyOrEnd
                 && frame.State != ContainerState.ObjectCommaOrEnd)
            _diagnostics.Add(range, "Expected ',' or '}' after object property.");

        _stack.RemoveAt(_stack.Count - 1);
        CompleteValue(range);
    }

    private void CloseArray(TextRange range)
    {
        if (_stack.Count == 0 || CurrentFrame.Kind != ContainerKind.Array)
        {
            _diagnostics.Add(range, "Unexpected ']'.");
            return;
        }

        Frame frame = CurrentFrame;
        if (frame.State == ContainerState.ArrayValueOrEnd && frame.AfterComma)
            _diagnostics.Add(range, "Trailing commas are not allowed in JSON arrays.");
        else if (frame.State != ContainerState.ArrayValueOrEnd
                 && frame.State != ContainerState.ArrayCommaOrEnd)
            _diagnostics.Add(range, "Expected ',' or ']' after array item.");

        _stack.RemoveAt(_stack.Count - 1);
        CompleteValue(range);
    }

    private void HandleColon(TextRange range)
    {
        if (_stack.Count > 0
            && CurrentFrame.Kind == ContainerKind.Object
            && CurrentFrame.State == ContainerState.ObjectColon)
        {
            SetCurrentFrame(ContainerState.ObjectValue);
            return;
        }

        _diagnostics.Add(range, "Unexpected ':'.");
    }

    private void HandleComma(TextRange range)
    {
        if (_stack.Count == 0)
        {
            _diagnostics.Add(range, "Unexpected ','.");
            return;
        }

        Frame frame = CurrentFrame;
        if (frame.Kind == ContainerKind.Object && frame.State == ContainerState.ObjectCommaOrEnd)
        {
            SetCurrentFrame(ContainerState.ObjectPropertyOrEnd, afterComma: true);
            return;
        }

        if (frame.Kind == ContainerKind.Array && frame.State == ContainerState.ArrayCommaOrEnd)
        {
            SetCurrentFrame(ContainerState.ArrayValueOrEnd, afterComma: true);
            return;
        }

        _diagnostics.Add(range,
            frame.Kind == ContainerKind.Object
                ? "Expected object property before ','."
                : "Expected array item before ','.");
    }

    private void HandleScalar(ScalarKind kind, TextRange range)
    {
        if (_stack.Count == 0)
        {
            if (_rootHasValue)
                _diagnostics.Add(range, "Only one top-level JSON value is allowed.");
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
                        SetCurrentFrame(ContainerState.ObjectColon);
                    }
                    else
                    {
                        _diagnostics.Add(range, "Expected property name.");
                    }
                    return;

                case ContainerState.ObjectColon:
                    _diagnostics.Add(range, "Expected ':' after property name.");
                    SetCurrentFrame(ContainerState.ObjectCommaOrEnd);
                    return;

                case ContainerState.ObjectValue:
                    SetCurrentFrame(ContainerState.ObjectCommaOrEnd);
                    return;

                case ContainerState.ObjectCommaOrEnd:
                    _diagnostics.Add(range, "Expected ',' or '}' after object property.");
                    return;
            }
        }

        if (frame.Kind == ContainerKind.Array)
        {
            if (frame.State == ContainerState.ArrayValueOrEnd)
            {
                SetCurrentFrame(ContainerState.ArrayCommaOrEnd);
            }
            else
            {
                _diagnostics.Add(range, "Expected ',' or ']' after array item.");
            }
        }
    }

    private bool CanStartValue(TextPosition start)
    {
        if (_stack.Count == 0)
        {
            if (_rootHasValue)
            {
                _diagnostics.Add(new TextRange(start, new TextPosition(start.Line, start.Column + 1)),
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

        _diagnostics.Add(new TextRange(start, new TextPosition(start.Line, start.Column + 1)),
            frame.Kind == ContainerKind.Object
                ? ExpectedObjectMessage(frame.State)
                : "Expected ',' or ']' after array item.");
        return false;
    }

    private void CompleteValue(TextRange range)
    {
        if (_stack.Count == 0)
        {
            if (_rootHasValue)
                _diagnostics.Add(range, "Only one top-level JSON value is allowed.");
            else
                _rootHasValue = true;
            return;
        }

        Frame frame = CurrentFrame;
        if (frame.Kind == ContainerKind.Object && frame.State == ContainerState.ObjectValue)
        {
            SetCurrentFrame(ContainerState.ObjectCommaOrEnd);
            return;
        }

        if (frame.Kind == ContainerKind.Array && frame.State == ContainerState.ArrayValueOrEnd)
            SetCurrentFrame(ContainerState.ArrayCommaOrEnd);
    }

    private Frame CurrentFrame
    {
        get => _stack[^1];
        set => _stack[^1] = value;
    }

    private void SetCurrentFrame(ContainerState state, bool afterComma = false)
    {
        Frame frame = CurrentFrame;
        CurrentFrame = frame with
        {
            State = state,
            AfterComma = afterComma
        };
    }

    private static string ExpectedObjectMessage(ContainerState state) => state switch
    {
        ContainerState.ObjectPropertyOrEnd => "Expected property name.",
        ContainerState.ObjectColon => "Expected ':' after property name.",
        ContainerState.ObjectValue => "Expected property value.",
        _ => "Expected ',' or '}' after object property.",
    };
}

internal sealed class JsonParser
{
    private readonly IReadOnlyList<JsonToken> _tokens;
    private readonly HashSet<TextRange> _propertyNameRanges = [];
    private int _index;

    public JsonParser(IReadOnlyList<JsonToken> tokens)
    {
        _tokens = tokens;
    }

    public IReadOnlySet<TextRange> PropertyNameRanges => _propertyNameRanges;

    public SyntaxNode ParseDocument()
    {
        TextPosition start = Current.Range.Start;
        SyntaxNode value = Current.Kind == JsonTokenKind.EndOfFile
            ? Missing()
            : ParseValue();

        while (Current.Kind != JsonTokenKind.EndOfFile)
        {
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
            JsonTokenKind.EndOfFile => Missing(),
            _ => ErrorNode(),
        };
    }

    private SyntaxNode ParseObject()
    {
        JsonToken open = Expect(JsonTokenKind.LeftBrace);
        var properties = new List<SyntaxNode>();

        if (Match(JsonTokenKind.RightBrace, out JsonToken close))
            return new SyntaxNode(JsonSyntaxKinds.Object, Combine(open.Range, close.Range), properties);

        while (Current.Kind != JsonTokenKind.EndOfFile)
        {
            if (Current.Kind != JsonTokenKind.String)
            {
                properties.Add(ErrorNode());
                if (!RecoverTo(JsonTokenKind.Comma, JsonTokenKind.RightBrace))
                    break;
            }
            else
            {
                JsonToken name = Advance();
                _propertyNameRanges.Add(name.Range);

                Expect(JsonTokenKind.Colon);
                SyntaxNode value = Current.Kind == JsonTokenKind.EndOfFile
                    ? Missing()
                    : ParseValue();

                properties.Add(new SyntaxNode(JsonSyntaxKinds.Property,
                    Combine(name.Range, value.Range), [Leaf(JsonSyntaxKinds.String, name), value]));
            }

            if (Match(JsonTokenKind.Comma, out _) && Current.Kind != JsonTokenKind.RightBrace)
                continue;

            if (Match(JsonTokenKind.RightBrace, out close))
                return new SyntaxNode(JsonSyntaxKinds.Object, Combine(open.Range, close.Range), properties);

            if (!RecoverTo(JsonTokenKind.Comma, JsonTokenKind.RightBrace))
                break;
        }

        return new SyntaxNode(JsonSyntaxKinds.Object, Combine(open.Range, Previous.Range), properties);
    }

    private SyntaxNode ParseArray()
    {
        JsonToken open = Expect(JsonTokenKind.LeftBracket);
        var items = new List<SyntaxNode>();

        if (Match(JsonTokenKind.RightBracket, out JsonToken close))
            return new SyntaxNode(JsonSyntaxKinds.Array, Combine(open.Range, close.Range), items);

        while (Current.Kind != JsonTokenKind.EndOfFile)
        {
            items.Add(ParseValue());

            if (Match(JsonTokenKind.Comma, out _) && Current.Kind != JsonTokenKind.RightBracket)
                continue;

            if (Match(JsonTokenKind.RightBracket, out close))
                return new SyntaxNode(JsonSyntaxKinds.Array, Combine(open.Range, close.Range), items);

            if (!RecoverTo(JsonTokenKind.Comma, JsonTokenKind.RightBracket))
                break;
        }

        return new SyntaxNode(JsonSyntaxKinds.Array, Combine(open.Range, Previous.Range), items);
    }

    private bool RecoverTo(JsonTokenKind first, JsonTokenKind second)
    {
        while (Current.Kind != JsonTokenKind.EndOfFile)
        {
            if (Current.Kind == first || Current.Kind == second)
                return true;
            Advance();
        }

        return false;
    }

    private SyntaxNode Missing()
    {
        return new SyntaxNode(JsonSyntaxKinds.Missing, new TextRange(Current.Range.Start, Current.Range.Start), []);
    }

    private SyntaxNode ErrorNode()
    {
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

    private JsonToken Expect(JsonTokenKind kind)
    {
        if (Current.Kind == kind)
            return Advance();

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

    private static TextRange Combine(TextRange start, TextRange end) =>
        new(start.Start, end.End);
}

internal static class JsonDiagnosticsAnalyzer
{
    private const int SegmentSize = 1 * 1024 * 1024;
    private const int MaxDiagnostics = 1000;
    private const long ProgressReportInterval = 16 * 1024 * 1024;
    private static readonly TimeSpan ProgressReportMinimumInterval = TimeSpan.FromMilliseconds(100);

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

    private sealed class Analyzer
    {
        private readonly SourceCursor _cursor;
        private readonly IProgress<LanguageDiagnosticsProgress>? _progress;
        private readonly CancellationToken _cancellationToken;
        private readonly JsonDiagnosticSink _diagnostics = new(MaxDiagnostics);
        private readonly JsonGrammarValidator _grammar;
        private readonly Stopwatch _progressStopwatch = Stopwatch.StartNew();
        private long _nextProgressReport = ProgressReportInterval;
        private TimeSpan _lastProgressReportElapsed;
        private int _cancellationCheckCountdown = 4096;

        public Analyzer(
            ILanguageTextSource source,
            IProgress<LanguageDiagnosticsProgress>? progress,
            CancellationToken cancellationToken)
        {
            _cursor = new SourceCursor(source, cancellationToken);
            _progress = progress;
            _cancellationToken = cancellationToken;
            _grammar = new JsonGrammarValidator(_diagnostics);
        }

        public bool HasMoreDiagnostics => _diagnostics.HasMoreDiagnostics;

        public IReadOnlyList<ParseDiagnostic> Analyze()
        {
            try
            {
                ReportProgress(force: true);
                while (!_cursor.IsAtEnd)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    ReportProgress(force: false);

                    if (_cursor.IsAtLineEnd)
                    {
                        if (_cursor.IsAtLastLine)
                            break;

                        _cursor.AdvanceLine();
                        continue;
                    }

                    char current = _cursor.Current;
                    if (current is ' ' or '\t')
                    {
                        SkipInlineWhitespace();
                        continue;
                    }

                    if (IsJsonWhitespace(current))
                    {
                        _cursor.Advance();
                        continue;
                    }

                    TextPosition start = _cursor.Position;
                    switch (current)
                    {
                        case '{':
                            _cursor.Advance();
                            Accept(JsonTokenKind.LeftBrace, start, _cursor.Position);
                            break;
                        case '}':
                            _cursor.Advance();
                            Accept(JsonTokenKind.RightBrace, start, _cursor.Position);
                            break;
                        case '[':
                            _cursor.Advance();
                            Accept(JsonTokenKind.LeftBracket, start, _cursor.Position);
                            break;
                        case ']':
                            _cursor.Advance();
                            Accept(JsonTokenKind.RightBracket, start, _cursor.Position);
                            break;
                        case ':':
                            _cursor.Advance();
                            Accept(JsonTokenKind.Colon, start, _cursor.Position);
                            break;
                        case ',':
                            _cursor.Advance();
                            Accept(JsonTokenKind.Comma, start, _cursor.Position);
                            break;
                        case '"':
                            LexString();
                            break;
                        case '-':
                            LexNumber();
                            break;
                        default:
                            if (IsDigit(current))
                                LexNumber();
                            else if (IsAsciiLetter(current))
                                LexLiteral();
                            else
                                LexUnexpectedCharacter();
                            break;
                    }
                }

                _grammar.Finish(_cursor.Position);
                ReportProgress(force: true);
                return _diagnostics.Diagnostics;
            }
            finally
            {
                _cursor.Dispose();
            }
        }

        private void LexString()
        {
            TextPosition start = _cursor.Position;
            _cursor.Advance();

            while (!_cursor.IsAtEnd)
            {
                CheckCancellationAndProgress();
                ReadOnlySpan<char> span = _cursor.RemainingTextOnLine;
                if (span.Length > 0)
                {
                    int stop = span.IndexOfAny('"', '\\');
                    if (stop < 0)
                    {
                        _cursor.AdvanceBy(span.Length);
                        continue;
                    }

                    if (stop > 0)
                        _cursor.AdvanceBy(stop);
                }

                if (_cursor.IsAtLineEnd)
                {
                    AddDiagnostic(new TextRange(start, _cursor.Position), "String literal is not terminated.");
                    Accept(JsonTokenKind.String, start, _cursor.Position);
                    return;
                }

                char current = _cursor.Current;
                if (current == '"')
                {
                    _cursor.Advance();
                    Accept(JsonTokenKind.String, start, _cursor.Position);
                    return;
                }

                if (current == '\\')
                {
                    _cursor.Advance();
                    if (_cursor.IsAtEnd || _cursor.IsAtLineEnd)
                    {
                        AddDiagnostic(new TextRange(start, _cursor.Position), "String literal is not terminated.");
                        Accept(JsonTokenKind.String, start, _cursor.Position);
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
            Accept(JsonTokenKind.String, start, _cursor.Position);
        }

        private void LexNumber()
        {
            TextPosition start = _cursor.Position;
            if (_cursor.Current == '-')
                _cursor.Advance();

            if (_cursor.IsAtEnd || _cursor.IsAtLineEnd || !IsDigit(_cursor.Current))
            {
                AddDiagnostic(new TextRange(start, _cursor.Position), "Expected digit after minus sign.");
                Accept(JsonTokenKind.Number, start, _cursor.Position);
                return;
            }

            if (_cursor.Current == '0')
            {
                _cursor.Advance();
                if (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && IsDigit(_cursor.Current))
                    AddDiagnostic(new TextRange(start, _cursor.Position), "JSON numbers cannot contain leading zeroes.");
            }
            else
            {
                ReadDigits();
            }

            if (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && _cursor.Current == '.')
            {
                _cursor.Advance();
                if (_cursor.IsAtEnd || _cursor.IsAtLineEnd || !IsDigit(_cursor.Current))
                    AddDiagnostic(new TextRange(start, _cursor.Position), "Expected digit after decimal point.");
                ReadDigits();
            }

            if (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && _cursor.Current is 'e' or 'E')
            {
                _cursor.Advance();
                if (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && _cursor.Current is '+' or '-')
                    _cursor.Advance();

                if (_cursor.IsAtEnd || _cursor.IsAtLineEnd || !IsDigit(_cursor.Current))
                    AddDiagnostic(new TextRange(start, _cursor.Position), "Expected digit in exponent.");
                ReadDigits();
            }

            Accept(JsonTokenKind.Number, start, _cursor.Position);
        }

        private void LexLiteral()
        {
            TextPosition start = _cursor.Position;
            var text = new StringBuilder();
            while (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd && IsAsciiLetter(_cursor.Current))
            {
                CheckCancellationAndProgress();
                text.Append(_cursor.Current);
                _cursor.Advance();
            }

            string value = text.ToString();
            JsonTokenKind kind = value switch
            {
                "true" => JsonTokenKind.True,
                "false" => JsonTokenKind.False,
                "null" => JsonTokenKind.Null,
                _ => JsonTokenKind.InvalidLiteral,
            };
            if (kind == JsonTokenKind.InvalidLiteral)
                AddDiagnostic(new TextRange(start, _cursor.Position), $"Unexpected literal '{value}'.");

            Accept(kind, start, _cursor.Position);
        }

        private void LexUnexpectedCharacter()
        {
            TextPosition start = _cursor.Position;
            char current = _cursor.Current;
            _cursor.Advance();
            AddDiagnostic(new TextRange(start, _cursor.Position), $"Unexpected character '{current}'.");
        }

        private void Accept(JsonTokenKind kind, TextPosition start, TextPosition end) =>
            _grammar.Accept(kind, new TextRange(start, end));

        private void ReadDigits()
        {
            while (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd)
            {
                ReadOnlySpan<char> span = _cursor.RemainingTextOnLine;
                int count = 0;
                while (count < span.Length && IsDigit(span[count]))
                    count++;

                if (count == 0)
                    return;

                _cursor.AdvanceBy(count);
                CheckCancellationAndProgress();
                if (count < span.Length)
                    return;
            }
        }

        private void SkipInlineWhitespace()
        {
            while (!_cursor.IsAtEnd && !_cursor.IsAtLineEnd)
            {
                ReadOnlySpan<char> span = _cursor.RemainingTextOnLine;
                int count = 0;
                while (count < span.Length && span[count] is ' ' or '\t')
                    count++;

                if (count == 0)
                    return;

                _cursor.AdvanceBy(count);
                CheckCancellationAndProgress();
                if (count < span.Length)
                    return;
            }
        }

        private void AddDiagnostic(TextRange range, string message)
            => _diagnostics.Add(range, message);

        private void CheckCancellationAndProgress()
        {
            _cancellationCheckCountdown--;
            if (_cancellationCheckCountdown > 0)
                return;

            _cancellationCheckCountdown = 4096;
            _cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(force: false);
        }

        private void ReportProgress(bool force)
        {
            if (_progress == null)
                return;

            if (!force)
            {
                if (_cursor.CharactersRead < _nextProgressReport)
                    return;
                TimeSpan elapsed = _progressStopwatch.Elapsed;
                if (elapsed - _lastProgressReportElapsed < ProgressReportMinimumInterval)
                    return;
                _lastProgressReportElapsed = elapsed;
            }
            else
            {
                _lastProgressReportElapsed = _progressStopwatch.Elapsed;
            }

            _nextProgressReport = _cursor.CharactersRead + ProgressReportInterval;
            _progress.Report(new LanguageDiagnosticsProgress(
                _cursor.CharactersRead,
                _cursor.TotalCharacters));
        }
    }

    private sealed class SourceCursor : IDisposable
    {
        private readonly ILanguageTextSource _source;
        private readonly ILanguageTextStream? _stream;
        private readonly CancellationToken _cancellationToken;
        private string _segment = "";
        private int _segmentStartColumn = -1;
        private int _segmentIndex;
        private int _lineLength = -1;
        private bool _segmentEndsAtLineEnd;
        private bool _streamAtLineEnd;
        private bool _streamEnded;
        private bool _disposed;

        public SourceCursor(ILanguageTextSource source, CancellationToken cancellationToken)
        {
            _source = source;
            _cancellationToken = cancellationToken;
            TotalCharacters = source.CharCountWithoutLineEndings;
            if (source is ILanguageTextStreamSource streamSource
                && streamSource.TryCreateTextStream(0, source.LineCount, out ILanguageTextStream stream))
            {
                _stream = stream;
            }
        }

        public int Line { get; private set; }
        public int Column { get; private set; }
        public long CharactersRead { get; private set; }
        public long TotalCharacters { get; }
        public bool IsAtEnd => _stream != null ? _streamEnded : Line >= _source.LineCount;
        public bool IsAtLastLine => Line >= _source.LineCount - 1;
        public TextPosition Position => new(Line, Column);
        public ReadOnlySpan<char> RemainingTextOnLine
        {
            get
            {
                EnsureSegment();
                if (_streamEnded || _streamAtLineEnd || _segmentIndex >= _segment.Length)
                    return ReadOnlySpan<char>.Empty;

                ReadOnlySpan<char> span = _segment.AsSpan(_segmentIndex);
                if (_stream != null)
                {
                    int lineBreak = span.IndexOfAny('\r', '\n');
                    if (lineBreak >= 0)
                        span = span[..lineBreak];
                }

                return span;
            }
        }

        public bool IsAtLineEnd
        {
            get
            {
                if (_stream != null)
                {
                    EnsureSegment();
                    return _streamEnded
                        || _streamAtLineEnd
                        || (_segmentIndex < _segment.Length && _segment[_segmentIndex] is '\r' or '\n')
                        || _segmentIndex >= _segment.Length;
                }

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
            if (_stream != null)
            {
                EnsureStreamSegment();
                if (_streamEnded
                    || _streamAtLineEnd
                    || _segmentIndex >= _segment.Length
                    || _segment[_segmentIndex] is '\r' or '\n')
                {
                    return;
                }

                AdvanceBy(1);
                return;
            }

            if (IsAtEnd || Column >= CurrentLineLength)
                return;

            EnsureSegment();
            AdvanceBy(1);
        }

        public void AdvanceBy(int count)
        {
            if (count <= 0)
                return;

            Column += count;
            _segmentIndex += count;
            CharactersRead += count;
            if (_stream != null
                && _segmentIndex >= _segment.Length
                && _segmentEndsAtLineEnd)
            {
                _streamAtLineEnd = true;
            }
        }

        public void AdvanceLine()
        {
            if (IsAtEnd)
                return;

            if (_stream != null)
            {
                EnsureSegment();
                if (_streamEnded)
                    return;

                Line++;
                Column = 0;
                if (_segmentIndex < _segment.Length && _segment[_segmentIndex] == '\r')
                {
                    _segmentIndex++;
                    if (_segmentIndex < _segment.Length && _segment[_segmentIndex] == '\n')
                        _segmentIndex++;
                }
                else if (_segmentIndex < _segment.Length && _segment[_segmentIndex] == '\n')
                {
                    _segmentIndex++;
                }

                _streamAtLineEnd = false;
                if (_segmentIndex >= _segment.Length)
                    ClearStreamSegment();
                return;
            }

            int unread = Math.Max(0, CurrentLineLength - Column);
            CharactersRead += unread;
            Line++;
            Column = 0;
            _lineLength = -1;
            _segment = "";
            _segmentStartColumn = -1;
            _segmentIndex = 0;
        }

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
            if (_stream != null)
            {
                EnsureStreamSegment();
                return;
            }

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
            _segment = ReadSourceSegment(Line, Column, count);
            _segmentStartColumn = Column;
            _segmentIndex = 0;
        }

        private void EnsureStreamSegment()
        {
            if (_streamEnded || _streamAtLineEnd)
                return;

            if (_segmentIndex < _segment.Length)
                return;

            while (_segmentIndex >= _segment.Length)
            {
                LanguageTextReadSegment segment = ReadStreamSegment(SegmentSize);
                if (segment.IsEnd)
                {
                    _streamEnded = true;
                    _segment = "";
                    _segmentStartColumn = Column;
                    _segmentIndex = 0;
                    _segmentEndsAtLineEnd = true;
                    return;
                }

                Line = segment.Line;
                Column = segment.StartColumn;
                _segment = segment.Text;
                _segmentStartColumn = Column;
                _segmentIndex = 0;
                _segmentEndsAtLineEnd = segment.EndsAtLineEnd;
                _streamAtLineEnd = segment.Text.Length == 0 && segment.EndsAtLineEnd;
                if (_segment.Length > 0 || _streamAtLineEnd)
                    return;
            }
        }

        private void ClearStreamSegment()
        {
            _segment = "";
            _segmentStartColumn = -1;
            _segmentIndex = 0;
            _segmentEndsAtLineEnd = false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _stream?.Dispose();
        }

        private LanguageTextReadSegment ReadStreamSegment(int count)
        {
            long start = Stopwatch.GetTimestamp();
            LanguageTextReadSegment segment = _stream!.ReadSegment(count, _cancellationToken);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            DiagnosticsPerformanceTrace.RecordSourceRead(elapsed, segment.Text.Length);
            return segment;
        }

        private string ReadSourceSegment(int line, int column, int count)
        {
            long start = Stopwatch.GetTimestamp();
            string segment = _source.GetLineSegment(line, column, count);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            DiagnosticsPerformanceTrace.RecordSourceRead(elapsed, segment.Length);
            return segment;
        }
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsDigit(char value) =>
        value is >= '0' and <= '9';

    private static bool IsAsciiLetter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsJsonWhitespace(char value) =>
        value is ' ' or '\t' or '\r' or '\n';
}
