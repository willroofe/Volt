using System.Text;

namespace Volt;

public readonly record struct TextPoint(long Line, long Column);

public readonly record struct TextRange(TextPoint Start, TextPoint End);

public sealed class TextDocumentChangedEventArgs(
    TextRange range,
    long lineDelta,
    long version) : EventArgs
{
    public TextRange Range { get; } = range;
    public long LineDelta { get; } = lineDelta;
    public long Version { get; } = version;
}

/// <summary>
/// Size-independent text document contract. The editor still consumes the
/// compatibility members while rendering and edit code are migrated off
/// <see cref="TextBuffer"/>.
/// </summary>
public interface ITextDocument
{
    long LineCount { get; }
    int Count { get; }
    long CharCount { get; }
    long EditGeneration { get; }
    int MaxLineLength { get; }
    string LineEnding { get; }
    string LineEndingDisplay { get; }
    Encoding Encoding { get; set; }
    bool IsDirty { get; set; }

    string this[int index] { get; set; }

    event EventHandler? DirtyChanged;
    event EventHandler<TextDocumentChangedEventArgs>? Changed;

    string GetLineSlice(long line, long startColumn, int maxChars);
    string GetText(TextRange range, int maxChars = int.MaxValue);
    void Insert(TextPoint point, string text);
    void Delete(TextRange range);
    void Replace(TextRange range, string text);

    int UpdateMaxForLine(int lineIndex);
    void InvalidateMaxLineLength();
    void NotifyLineChanging(int lineIndex);
    void InsertLine(int index, string line);
    void RemoveAt(int index);
    void RemoveRange(int index, int count);
    List<string> GetLines(int start, int count);
    void ReplaceLines(int start, int removeCount, List<string> newLines);
    void InsertAt(int line, int col, string text);
    void DeleteAt(int line, int col, int length);
    void ReplaceAt(int line, int col, int length, string text);
    void JoinWithNext(int line);
    string TruncateAt(int line, int col);
    int AppendContent(string text, int tabSize);
    string GetContent();
    void Clear();
    Task SaveAsync(string path, CancellationToken cancellationToken = default);
}
