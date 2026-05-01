using Xunit;

namespace Volt.Tests;

public class DisposableResourceSwapTests
{
    [Fact]
    public void Replace_SuccessSwapsCurrentAndDisposesPrevious()
    {
        var previous = new DisposableProbe();
        var replacement = new DisposableProbe();
        DisposableProbe? current = previous;

        DisposableResourceSwap.Replace(ref current, () => replacement);

        Assert.Same(replacement, current);
        Assert.True(previous.IsDisposed);
        Assert.False(replacement.IsDisposed);
    }

    [Fact]
    public void Replace_FactoryFailureKeepsCurrent()
    {
        var previous = new DisposableProbe();
        DisposableProbe? current = previous;

        Assert.Throws<InvalidOperationException>(() =>
            DisposableResourceSwap.Replace<DisposableProbe>(ref current, () => throw new InvalidOperationException()));

        Assert.Same(previous, current);
        Assert.False(previous.IsDisposed);
    }

    private sealed class DisposableProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
