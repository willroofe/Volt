using System.Text;

namespace Volt;

internal static class DocumentFactory
{
    public static async Task<ITextDocument> OpenAsync(
        string path,
        int tabSize,
        CancellationToken cancellationToken = default)
    {
        var encoding = FileHelper.DetectEncoding(path);
        if (encoding.CodePage != Encoding.UTF8.CodePage)
            return FromText(FileHelper.ReadAllText(path, encoding), tabSize, encoding);
        return await PieceTreeTextDocument.OpenAsync(path, encoding, tabSize, cancellationToken);
    }

    public static ITextDocument FromText(string text, int tabSize, Encoding? encoding = null)
    {
        var document = new TextBuffer { Encoding = encoding ?? new UTF8Encoding(false) };
        document.SetContent(text, tabSize);
        return document;
    }
}
