using Volt;
using System.Windows.Controls;
using Xunit;

namespace Volt.Tests;

public class FindBarSelectionTests
{
    [Fact]
    public void ResolveFindInSelectionBounds_PrefersCurrentSelection()
    {
        var current = (2, 3, 4, 5);
        var captured = (0, 1, 0, 8);

        var resolved = FindBar.ResolveFindInSelectionBounds(current, captured);

        Assert.Equal(current, resolved);
    }

    [Fact]
    public void ResolveFindInSelectionBounds_FallsBackToCapturedSelection()
    {
        var captured = (0, 1, 0, 8);

        var resolved = FindBar.ResolveFindInSelectionBounds(null, captured);

        Assert.Equal(captured, resolved);
    }

    [Fact]
    public void ShouldApplyLiveSelectionBounds_IgnoresCollapsedSelection()
    {
        var current = (0, 1, 0, 8);

        bool shouldApply = FindBar.ShouldApplyLiveSelectionBounds(null, current);

        Assert.False(shouldApply);
    }

    [Fact]
    public void ShouldApplyLiveSelectionBounds_AppliesChangedSelection()
    {
        var current = (0, 1, 0, 8);
        var next = (2, 0, 2, 10);

        bool shouldApply = FindBar.ShouldApplyLiveSelectionBounds(next, current);

        Assert.True(shouldApply);
    }

    [Fact]
    public void ResolveMatchCountLabel_KeepsCurrentTextForBlankStatus()
    {
        string resolved = FindBar.ResolveMatchCountLabel(
            currentText: "1 of 12",
            statusText: "",
            hasQuery: true);

        Assert.Equal("1 of 12", resolved);
    }

    [Fact]
    public void ResolveMatchCountLabel_KeepsCurrentTextForSearchingStatus()
    {
        string resolved = FindBar.ResolveMatchCountLabel(
            currentText: "1 of 12",
            statusText: "Searching...",
            hasQuery: true);

        Assert.Equal("1 of 12", resolved);
    }

    [Fact]
    public void ResolveMatchCountLabel_KeepsCurrentTextForPlaceholderCountStatus()
    {
        string resolved = FindBar.ResolveMatchCountLabel(
            currentText: "1 of 12",
            statusText: "... of ...",
            hasQuery: true);

        Assert.Equal("1 of 12", resolved);
    }

    [Fact]
    public void ResolveMatchCountLabel_ClearsBlankStatusWithoutQuery()
    {
        string resolved = FindBar.ResolveMatchCountLabel(
            currentText: "1 of 12",
            statusText: "",
            hasQuery: false);

        Assert.Equal("", resolved);
    }

    [Fact]
    public void ResolveMatchCountLabel_UsesNonEmptyStatus()
    {
        string resolved = FindBar.ResolveMatchCountLabel(
            currentText: "1 of 12",
            statusText: "No results",
            hasQuery: true);

        Assert.Equal("No results", resolved);
    }

    [StaFact]
    public void ModeButtonActiveState_RoundTripsThroughSingleVisualFlag()
    {
        var button = new Button();

        FindBar.SetModeButtonActive(button, active: true);

        Assert.True(FindBar.IsModeButtonActive(button));

        FindBar.SetModeButtonActive(button, active: false);

        Assert.False(FindBar.IsModeButtonActive(button));
    }
}
