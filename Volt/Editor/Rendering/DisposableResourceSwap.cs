namespace Volt;

internal static class DisposableResourceSwap
{
    public static void Replace<T>(ref T? current, Func<T> create)
        where T : class, IDisposable
    {
        var replacement = create();
        var previous = current;
        current = replacement;
        previous?.Dispose();
    }
}
