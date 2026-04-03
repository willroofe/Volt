using Xunit;
using Volt;
using static Volt.UndoManager;

namespace Volt.Tests;

public class UndoManagerTests
{
    private static UndoEntry MakeEntry(int startLine = 0) =>
        new(startLine, ["before"], ["after"], 0, 0, 0, 5);

    [Fact]
    public void PushUndo_RestoresEntry()
    {
        var mgr = new UndoManager();
        var entry = MakeEntry();

        mgr.Push(entry);

        Assert.True(mgr.CanUndo);
        Assert.Equal(1, mgr.UndoCount);

        var restored = mgr.Undo();

        Assert.Same(entry, restored);
        Assert.False(mgr.CanUndo);
    }

    [Fact]
    public void Redo_RestoresAfterUndo()
    {
        var mgr = new UndoManager();
        mgr.Push(MakeEntry());

        mgr.Undo();

        Assert.True(mgr.CanRedo);

        var redone = mgr.Redo();

        Assert.NotNull(redone);
        Assert.True(mgr.CanUndo);
        Assert.False(mgr.CanRedo);
    }

    [Fact]
    public void Push_ClearsRedoStack()
    {
        var mgr = new UndoManager();
        mgr.Push(MakeEntry());
        mgr.Undo();

        Assert.True(mgr.CanRedo);

        mgr.Push(MakeEntry());

        Assert.False(mgr.CanRedo);
    }

    [Fact]
    public void Push_EvictsOldestAt200()
    {
        var mgr = new UndoManager();
        for (int i = 0; i < 200; i++)
            Assert.False(mgr.Push(MakeEntry(i)));

        Assert.Equal(200, mgr.UndoCount);

        Assert.True(mgr.Push(MakeEntry(200)));

        Assert.Equal(200, mgr.UndoCount);
    }

    [Fact]
    public void Clear_ResetsBothStacks()
    {
        var mgr = new UndoManager();
        mgr.Push(MakeEntry());
        mgr.Push(MakeEntry());
        mgr.Undo();

        mgr.Clear();

        Assert.False(mgr.CanUndo);
        Assert.False(mgr.CanRedo);
        Assert.Equal(0, mgr.UndoCount);
    }
}
