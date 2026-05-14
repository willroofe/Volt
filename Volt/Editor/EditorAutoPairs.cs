namespace Volt;

internal static class EditorAutoPairs
{
    public static bool TryGetCloser(char opener, out char closer)
    {
        switch (opener)
        {
            case '(':
                closer = ')';
                return true;
            case '{':
                closer = '}';
                return true;
            case '[':
                closer = ']';
                return true;
            default:
                closer = '\0';
                return false;
        }
    }

    public static bool IsQuote(char ch) => ch is '"' or '\'';

    public static bool IsOvertypeCharacter(char ch)
        => ch is ')' or '}' or ']' || IsQuote(ch);

    public static bool IsEmptyPair(char beforeCaret, char afterCaret)
        => TryGetCloser(beforeCaret, out char expectedCloser) && afterCaret == expectedCloser
           || IsQuote(beforeCaret) && afterCaret == beforeCaret;
}
