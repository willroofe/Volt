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

    public JsonLexer(string text)
    {
        _text = text;
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
