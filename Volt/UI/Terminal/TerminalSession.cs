// Volt/UI/Terminal/TerminalSession.cs
using System;
using System.IO;

namespace Volt;

/// <summary>
/// Composition root for one terminal session — wires together the pty, parser,
/// dispatcher, grid, and view. Created and owned by TerminalPanel.
///
/// The pty is NOT spawned in the constructor. It's spawned on the first
/// SizeRequested from the view, so pwsh starts with the correct terminal size
/// rather than our 24x80 default and then being resized mid-init (which confuses
/// PSReadLine's stored anchor row).
/// </summary>
public sealed class TerminalSession : IDisposable
{
    /// <summary>Short label for the session tab (e.g. PowerShell, Command Prompt), not the executable path.</summary>
    public string Title { get; }
    public event Action<int>? Exited;

    public TerminalGrid Grid { get; }
    public PtySession? Pty { get; private set; }
    public VtStateMachine Parser { get; }
    public VtDispatcher Dispatcher { get; }
    public TerminalView View { get; }

    private readonly string _shellExe;
    private readonly string? _args;
    private readonly string _cwd;

    /// <summary>Process working directory passed to the pseudoconsole.</summary>
    public string WorkingDirectory => _cwd;

    public TerminalSession(string shellExe, string? args, string cwd, short rows, short cols, int scrollbackLines)
    {
        _shellExe = shellExe;
        _args = args;
        _cwd = cwd;
        Title = ShellTabLabel(shellExe);

        Grid = new TerminalGrid(rows, cols, scrollbackLines);
        Dispatcher = new VtDispatcher(Grid);
        Parser = new VtStateMachine(Dispatcher);
        View = new TerminalView { Grid = Grid };

        View.InputBytes += bytes => Pty?.Write(bytes);
        View.SizeRequested += OnViewSizeRequested;
    }

    private void OnViewSizeRequested(int rows, int cols)
    {
        if (Pty == null)
        {
            // First layout — spawn the pty with the actual view size
            StartPty((short)rows, (short)cols);
        }
        else
        {
            // Subsequent resize — forward to the running pty
            Pty.Resize((short)rows, (short)cols);
        }
    }

    private void StartPty(short rows, short cols)
    {
        var pty = new PtySession(_shellExe, _args, _cwd, rows, cols);
        pty.Output += bytes => Parser.Feed(bytes.Span);
        pty.Exited += code => Exited?.Invoke(code);
        Dispatcher.ResponseRequested += resp => pty.Write(resp);
        Pty = pty;
    }

    public void Dispose()
    {
        try { Pty?.Dispose(); } catch { }
    }

    /// <summary>Maps the shell executable to a short tab title; ignores VT OSC title sequences.</summary>
    internal static string ShellTabLabel(string shellExe)
    {
        if (string.IsNullOrWhiteSpace(shellExe)) return "Shell";
        var trimmed = shellExe.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var file = Path.GetFileName(trimmed);
        if (file.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)) return "PowerShell";
        if (file.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)) return "PowerShell";
        if (file.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)) return "Command Prompt";
        return Path.GetFileNameWithoutExtension(file);
    }
}
