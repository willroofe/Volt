// Volt/UI/Terminal/TerminalSession.cs
using System;

namespace Volt;

/// <summary>
/// Composition root for one terminal session — wires together the pty, parser,
/// dispatcher, grid, and view. Created and owned by TerminalPanel.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    public string Title { get; private set; } = "Terminal";
    public event Action? TitleChanged;
    public event Action<int>? Exited;

    public TerminalGrid Grid { get; }
    public PtySession Pty { get; }
    public VtStateMachine Parser { get; }
    public VtDispatcher Dispatcher { get; }
    public TerminalView View { get; }

    public TerminalSession(string shellExe, string? args, string cwd, short rows, short cols, int scrollbackLines)
    {
        Grid = new TerminalGrid(rows, cols, scrollbackLines);
        Dispatcher = new VtDispatcher(Grid);
        Parser = new VtStateMachine(Dispatcher);
        View = new TerminalView { Grid = Grid };
        Pty = new PtySession(shellExe, args, cwd, rows, cols);

        // Wire the pipelines
        Pty.Output += bytes => Parser.Feed(bytes.Span);
        Pty.Exited += code => Exited?.Invoke(code);
        Dispatcher.TitleChanged += t => { Title = t; TitleChanged?.Invoke(); };
        Dispatcher.ResponseRequested += resp => Pty.Write(resp);
        View.InputBytes += bytes => Pty.Write(bytes);
        View.SizeRequested += (r, c) => Pty.Resize((short)r, (short)c);
    }

    public void Dispose()
    {
        try { Pty.Dispose(); } catch { }
    }
}
