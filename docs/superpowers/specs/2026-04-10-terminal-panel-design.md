# Terminal Panel — Design

**Status:** Draft
**Date:** 2026-04-10
**Author:** Brainstormed with Claude

## Summary

Add an integrated terminal to Volt as a new dockable panel (`TerminalPanel`) that implements the existing `IPanel` interface. The panel hosts one or more shell sessions (PowerShell / cmd / etc.), each backed by a Windows pseudoconsole (ConPTY). All terminal emulation — VT parsing, grid buffer, scrollback, rendering — is built from scratch in idiomatic C#, studying Microsoft Terminal's implementation as a quality reference but not porting its code. The terminal reuses Volt's existing `FontManager.DrawGlyphRun` path for rendering and integrates with Volt's theming system for colors.

## Goals

- A terminal that feels as responsive as Windows Terminal for everyday shell use (build scripts, git, interactive REPLs)
- Full-screen apps like `vim`, `less`, `htop` work correctly via alternate-screen-buffer support
- Multiple concurrent sessions inside one panel (Volt's panel system is single-instance per panel type, so this must be internal to the panel)
- Zero external dependencies — every line of the terminal stack is Volt code we own
- Bounded scope: v1 ships a correct-and-fast core, not feature parity with Windows Terminal

## Non-Goals (deferred beyond v1)

- Shell integration via OSC 633 (prompt marking, command decorations)
- Link detection with Ctrl+click
- In-terminal find/search
- Sixel / ReGIS / iTerm2 images
- Font ligatures
- Split terminals within the panel
- Session persistence across app restart (reattach to running shell)
- Bracketed paste mode
- Configurable keybindings beyond the built-in defaults
- "Send selection to terminal" from the editor

## Key Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Multi-session model | Multiple sessions hosted inside one `TerminalPanel` instance with an internal tab strip | Volt's panel system allows only one instance per panel type, so multi-session must be internal |
| ConPTY wrapper | Direct P/Invoke (~150 LOC), no external library | Pty.Net is a cross-platform abstraction; Volt is Windows-only, so the abstraction is pure overhead |
| VT parser | Hand-rolled Paul Williams state machine, studying Microsoft Terminal as reference | Avoids staleness risk of VtNetCore; the state machine is ~400–500 lines of well-documented design; matches Volt's "build it ourselves, stay lean" philosophy |
| Rendering | Custom `TerminalView : FrameworkElement` using `FontManager.DrawGlyphRun` | Matches `EditorControl` pattern; bypasses DWrite shaping for scroll performance |
| Default shell | Auto-detect `pwsh.exe` → `powershell.exe` → `cmd.exe` on first run; persist choice to `AppSettings.Editor.TerminalShellPath`; never re-detect | Sensible default + user control without ongoing re-detection churn |
| Starting directory | Single file open → file's directory. Single folder open → folder root. Multi-folder workspace with active tab → active file's parent dir. Multi-folder workspace with no active tab → first folder. Nothing open → `%USERPROFILE%` | Matches user's explicit rules |
| Shortcut routing when terminal is focused | VSCode-style allowlist — a hardcoded set of Volt shortcuts (command palette, tab switching, settings, panel toggles) intercept before reaching the shell; everything else goes to the shell | Only policy that matches user expectation from integrated terminals |
| Shell exit behavior | Close the tab automatically; if last session, show empty "click + to start a terminal" state | User choice |
| Scrollback size | 10,000 lines default | Between VSCode (1k) and Windows Terminal (9k); Volt users tend to have lots of RAM |
| Default dock placement | Bottom region | Convention; follows VSCode muscle memory |

## Architecture

### Module Layout

```
Volt/
├── Terminal/                           -- pure C#, no WPF references
│   ├── ConPty/
│   │   ├── ConPtyHost.cs               -- P/Invoke wrapper: CreatePseudoConsole, pipes, process launch
│   │   ├── NativeMethods.cs            -- P/Invoke signatures & structs (STARTUPINFOEX, etc.)
│   │   └── PtySession.cs               -- Owns process + pipes + read loop; exposes byte stream in/out
│   ├── Vt/
│   │   ├── VtStateMachine.cs           -- Paul Williams state machine
│   │   ├── VtDispatcher.cs             -- Parsed sequences → grid operations (CSI, SGR, OSC, DEC)
│   │   └── AnsiPalette.cs              -- 16/256/truecolor resolution, theme-integrated
│   ├── Buffer/
│   │   ├── TerminalGrid.cs             -- Main + alt screen buffer, scrollback ring, scroll region
│   │   ├── Cell.cs                     -- struct: glyph + fg/bg + attr flags
│   │   └── GridRegion.cs               -- Dirty-rect tracking for partial redraws
│   └── Input/
│       └── KeyEncoder.cs               -- WPF Key + modifiers → VT input sequences
├── UI/
│   └── Terminal/                       -- WPF layer
│       ├── TerminalPanel.xaml/.cs      -- IPanel implementation; multi-session tab strip + host
│       ├── TerminalView.cs             -- Custom FrameworkElement; renders a single TerminalGrid
│       └── TerminalSession.cs          -- Composition root for one (PtySession + parser + grid + view)
```

### Design Principles

- **`Terminal/` has zero WPF references** below `Terminal/Input/`. The ConPTY layer, parser, dispatcher, and grid are pure C# and unit-testable without spinning up WPF. Only `TerminalView`, `TerminalPanel`, and `KeyEncoder` touch WPF.
- **Single-responsibility modules:** `ConPty` knows nothing about VT sequences; `VtStateMachine` knows nothing about grids; `VtDispatcher` knows nothing about rendering; `TerminalView` knows nothing about VT sequences.
- **Matches existing Volt architecture:** the split mirrors how `EditorControl` hosts WPF while `TextBuffer`, `UndoManager`, `SyntaxManager` etc. are pure C# and tested separately.

## Data Flow

### Pipeline A — shell output → pixels (hot path)

```
shell stdout
    ↓
[ConPtyHost anonymous pipe, Read() on background Task]
    ↓ byte[]
[Dispatcher.BeginInvoke to UI thread, coalesced]
    ↓ ReadOnlySpan<byte>
[VtStateMachine.Feed(bytes)]
    ↓ parsed events (Print, Execute, CsiDispatch, OscDispatch, ...)
[VtDispatcher] ← per-event callbacks
    ↓ grid mutations (WriteCell, MoveCursor, Erase, SetSgr, SwitchAltBuffer, ...)
[TerminalGrid] ← tracks dirty rows in GridRegion
    ↓ raises Changed event (coalesced)
[TerminalView.OnGridChanged → InvalidateVisual on dirty rows only]
    ↓
[TerminalView.OnRender → FontManager.DrawGlyphRun per run of same-attr cells]
```

**Critical performance details:**

- **Background read task** reads into a pooled `byte[4096]` via `ArrayPool<byte>.Shared` and marshals to the UI thread via `Dispatcher.BeginInvoke(DispatcherPriority.Send)`.
- **Output coalescing:** the read loop posts at most one "drain pending bytes" callback at a time; subsequent reads append to a shared `ConcurrentQueue<byte[]>` and the drain callback consumes the whole queue. A process like `cargo build` can produce tens of MB/sec; without coalescing we'd queue thousands of `BeginInvoke` calls.
- **Render throttling:** `TerminalView` does not `InvalidateVisual` on every grid mutation. It coalesces via the same pattern as `EditorControl` — a render tick (`CompositionTarget.Rendering` or `DispatcherTimer` at ~60Hz). Multiple VT sequences in one frame → one repaint.
- **Dirty-rect rendering:** `GridRegion` tracks `minDirtyRow`/`maxDirtyRow` since last paint. `OnRender` only redraws the dirty row range, not the whole viewport.
- **Glyph run batching:** contiguous cells with identical `(fg, bg, attr)` are drawn as one `GlyphRun` via `FontManager.DrawGlyphRun`. Cell-by-cell drawing would re-trigger DWrite per character and tank scroll performance.

### Pipeline B — keyboard → shell stdin

```
WPF OnKeyDown / OnTextInput on TerminalView
    ↓ Key + ModifierKeys (or typed char)
[Allowlist check: is this a reserved Volt shortcut?]
    ├── yes → bubble up to MainWindow, don't consume
    └── no  → [KeyEncoder.Encode(key, mods) → byte[]]
                  ↓
              [PtySession.Write(bytes)]
                  ↓
              ConPtyHost anonymous pipe → shell stdin
```

`KeyEncoder` responsibilities:

- Printable characters via `OnTextInput` (handles IME, dead keys correctly — same pattern `EditorControl` uses)
- Special keys via `OnKeyDown`: arrows → `ESC [ A/B/C/D`, function keys → `ESC [ n ~` or SS3 forms, Home/End/PgUp/PgDn, Tab, Enter (as `\r`), Backspace (as `\x7f`)
- Modifier encoding: xterm modifier form for modified arrows (`ESC [ 1 ; m A`)
- Ctrl+letter → control byte (`Ctrl+A` → `0x01`)
- Alt+key → ESC prefix (meta)

**Shortcut allowlist.** `TerminalView` exposes an `AddAllowlistedShortcut(Key, ModifierKeys)` method. The canonical list is **not hardcoded in `TerminalView`** — `MainWindow` registers each global shortcut at startup by calling this method alongside the actual key binding, so the allowlist can never drift from the real bindings. The intended set of commands (whatever keys `MainWindow` actually binds them to) is:

- Command palette
- Switch editor tab (next/prev)
- Open settings
- Toggle file explorer panel
- Focus/toggle terminal panel
- View-menu region toggles (left/right/top/bottom dock visibility)

Additionally, `TerminalView` handles the following **itself**, not via the allowlist:

- `Ctrl+Shift+C` / `Ctrl+Shift+V` — terminal copy/paste; never forwarded to shell, never bubbled to `MainWindow`
- `Ctrl+C` with a selection → copy; `Ctrl+C` with no selection → forwarded to shell as `0x03` (SIGINT-equivalent). Matches Windows Terminal.

### Pipeline C — resize & lifecycle

```
TerminalPanel.SizeChanged / TerminalView.OnRenderSizeChanged
    ↓ new rows × cols (from pixel size ÷ cell size via FontManager)
[debounce: DispatcherTimer 50ms — avoid ResizePseudoConsole spam during drag]
    ↓
[TerminalGrid.Resize(rows, cols)] ← reflow, preserve cursor & scrollback
    ↓
[PtySession.Resize(rows, cols)] ← ResizePseudoConsole P/Invoke
    ↓
shell receives SIGWINCH-equivalent, redraws its UI
```

Shell exit (per v1 choice):

```
[background read loop hits EOF / ReadFile returns 0]
    ↓ Dispatcher.BeginInvoke
[TerminalSession.OnExited]
    ↓
[TerminalPanel.RemoveSession(session)]
    ↓
[if sessions.Count == 0] → panel shows empty state ("click + to start a terminal")
```

## Component Contracts

### `ConPty/ConPtyHost.cs` — static helpers

```csharp
static class ConPtyHost
{
    static PtyHandles Create(string shellExe, string? args, string cwd,
                             IDictionary<string, string>? env,
                             short rows, short cols);
}

readonly struct PtyHandles
{
    public IntPtr PseudoConsole { get; }     // HPCON
    public SafeFileHandle Input { get; }     // write end (we send to shell)
    public SafeFileHandle Output { get; }    // read end (we receive from shell)
    public Process Process { get; }
}
```

Handle cleanup on partial failure is critical: if `CreatePseudoConsole` succeeds but `Process.Start` fails, `Create` uses a local try/catch to dispose any handles created so far before rethrowing. `SafeFileHandle` helps but the HPCON is an `IntPtr` and needs explicit `ClosePseudoConsole`.

### `ConPty/PtySession.cs` — one pty + process lifetime

```csharp
sealed class PtySession : IDisposable
{
    public event Action<ReadOnlyMemory<byte>>? Output;  // fires on UI thread
    public event Action<int>? Exited;                   // exit code; fires on UI thread

    public PtySession(string shellExe, string? args, string cwd, short rows, short cols);

    public void Write(ReadOnlySpan<byte> data);         // stdin to shell
    public void Resize(short rows, short cols);         // ResizePseudoConsole
    public void Dispose();                              // clean shutdown
}
```

`Dispose` order: set cancellation flag → close pipe handles (unblocks `ReadFile` with `ERROR_BROKEN_PIPE`) → wait up to ~500ms for read task → kill process → dispose HPCON.

### `Vt/VtStateMachine.cs` — Paul Williams parser

Reference: https://vt100.net/emu/dec_ansi_parser

States: `Ground`, `Escape`, `EscapeIntermediate`, `CsiEntry`, `CsiParam`, `CsiIntermediate`, `CsiIgnore`, `OscString`, `DcsEntry`, `DcsParam`, `DcsIntermediate`, `DcsPassthrough`, `DcsIgnore`, `SosPmApcString`.

```csharp
interface IVtEventHandler
{
    void Print(char ch);
    void Execute(byte ctrl);
    void CsiDispatch(char final, ReadOnlySpan<int> params, ReadOnlySpan<char> intermediates);
    void EscDispatch(char final, ReadOnlySpan<char> intermediates);
    void OscDispatch(int command, string data);
    // DCS: state machine still enters/exits DCS states correctly (so sixel etc. are
    // consumed without corrupting downstream parsing), but no events are dispatched
    // to the handler in v1. Content is silently dropped.
}

sealed class VtStateMachine
{
    public VtStateMachine(IVtEventHandler handler);
    public void Feed(ReadOnlySpan<byte> bytes);
}
```

Parameters use `int` with a sentinel for "omitted" (e.g., `CSI ; 5 H` means `row=default, col=5`). UTF-8 decoding happens at the `Print` boundary — the state machine operates on bytes for C0/ESC/CSI/OSC handling but accumulates continuation bytes into a `char` for `Print`.

### `Vt/VtDispatcher.cs` — parsed sequences → grid operations

Implements `IVtEventHandler`. Holds a reference to `TerminalGrid` and a mutable "pen" (current fg/bg/attr). Handles:

- `Print` → write cell, advance cursor, wrap if needed
- `Execute` (LF/CR/BS/HT/BEL) → cursor movement or `grid.Bell()`
- CSI finals: `A/B/C/D/E/F/G/H/f` (cursor), `J/K` (erase), `L/M` (insert/delete lines), `P/@` (insert/delete chars), `S/T` (scroll), `m` (SGR), `r` (scroll region), `h/l` (set/reset mode including `?1049` alt buffer, `?25` cursor visibility), `n` (device status report — respond via `PtySession.Write`)
- OSC 0/1/2 → window title → raises `TitleChanged`
- OSC 4 (palette change) → ignored in v1
- Mouse mode enable (`?1000/1002/1006`) → deferred, ignored with state bit recorded so we don't get confused

### `Buffer/TerminalGrid.cs`

```csharp
sealed class TerminalGrid
{
    public int Rows { get; }
    public int Cols { get; }
    public int ScrollbackLines { get; }            // default 10_000
    public (int row, int col) Cursor { get; }
    public bool CursorVisible { get; set; }
    public bool UsingAltBuffer { get; private set; }

    public event Action? Changed;

    public ref Cell CellAt(int row, int col);      // row < 0 = scrollback

    public void WriteCell(int row, int col, char ch, CellAttr attr);
    public void EraseInLine(int row, EraseMode mode);
    public void EraseInDisplay(EraseMode mode);
    public void ScrollUp(int n);                   // scroll-region-aware
    public void ScrollDown(int n);
    public void InsertLines(int n);
    public void DeleteLines(int n);
    public void SetScrollRegion(int top, int bottom);
    public void SwitchToAltBuffer();               // save main state, enter alt
    public void SwitchToMainBuffer();              // discard alt, restore main
    public void Resize(int rows, int cols);        // reflow, preserve cursor
    public void Bell();

    public int DirtyMinRow { get; }
    public int DirtyMaxRow { get; }
    public void ClearDirty();
}

[Flags]
enum CellAttr : ushort
{
    None = 0,
    Bold = 1, Italic = 2, Underline = 4, Inverse = 8, Dim = 16,
}

struct Cell
{
    public char Glyph;
    public int FgIndex;
    public int BgIndex;
    public CellAttr Attr;
}
```

**Truecolor encoding:** to keep `Cell` at ~16 bytes, truecolor values are stored in a side table on the grid. `FgIndex < -1` indexes into a `List<uint>` of ARGB values. Most cells use palette colors; truecolor is the rare case, so the indirection cost is acceptable.

**Scrollback:** ring buffer of `Cell[Cols]` rows. Main-buffer rows scrolled off the top are pushed into scrollback. The alt buffer does not participate in scrollback (matching xterm — this is why `vim` doesn't pollute scrollback).

### `Vt/AnsiPalette.cs`

```csharp
static class AnsiPalette
{
    static Color Resolve(int index, bool isForeground);
    static Color ResolveTrueColor(uint argb);
    static uint DefaultFg { get; }
    static uint DefaultBg { get; }
}
```

ANSI 16 + default fg/bg pulled from a new theme JSON section. 256-color xterm cube and grayscale ramp are fixed tables (standard xterm values).

**Theme JSON addition** (to `Resources/Themes/*.json` and the `ColorTheme` model):

```json
{
  "terminal": {
    "background": "#1e1e1e",
    "foreground": "#d4d4d4",
    "ansi": [
      "#000000", "#cd3131", "#0dbc79", "#e5e510",
      "#2472c8", "#bc3fbc", "#11a8cd", "#e5e5e5",
      "#666666", "#f14c4c", "#23d18b", "#f5f543",
      "#3b8eea", "#d670d6", "#29b8db", "#e5e5e5"
    ]
  }
}
```

All three shipped themes (`default-dark.json`, `default-light.json`, `gruvbox-dark.json`) get a `terminal` section with appropriate defaults. If a theme lacks the section, a built-in 16-color table is used as fallback.

### `Input/KeyEncoder.cs`

```csharp
static class KeyEncoder
{
    static byte[]? Encode(Key key, ModifierKeys modifiers);  // null = pass through
    static byte[] EncodeChar(char ch);                        // for OnTextInput
}
```

### `UI/Terminal/TerminalSession.cs`

```csharp
sealed class TerminalSession : IDisposable
{
    public string Title { get; private set; }
    public event Action? TitleChanged;
    public event Action<int>? Exited;

    public TerminalGrid Grid { get; }
    public PtySession Pty { get; }
    public VtStateMachine Parser { get; }
    public VtDispatcher Dispatcher { get; }
    public TerminalView View { get; }

    public TerminalSession(string shellExe, string cwd, short rows, short cols);
    public void Dispose();
}
```

Pure composition root. Wires `Pty.Output` → `Parser.Feed` → `Dispatcher` → `Grid` → `View.Invalidate`.

### `UI/Terminal/TerminalView.cs`

Custom `FrameworkElement` (not `UserControl`) — follows the `EditorControl` pattern. One `DrawingVisual` for the grid, one for the cursor (so cursor blink doesn't re-render the grid). Implements `IScrollInfo` for scrollback scrolling. Font metrics via `FontManager`.

### `UI/Terminal/TerminalPanel.xaml.cs`

```csharp
sealed class TerminalPanel : UserControl, IPanel
{
    public string PanelId => "terminal";
    public string Title => _activeSession?.Title ?? "Terminal";
    public string? IconGlyph => "\uE756";
    public UIElement Content => this;
    public event Action? TitleChanged;

    public void NewSession();
    public void CloseActiveSession();
    public void FocusActiveSession();
}
```

XAML: two-row grid. Top row is a thin tab strip (small `ItemsControl` with tab items + "+" button). Bottom row is a `ContentPresenter` bound to `_activeSession.View`.

## Error Handling

**Rule:** never crash Volt on terminal errors. A broken session closes cleanly; the rest of the app is unaffected.

### ConPTY / process launch

| Failure | Handling |
|---|---|
| `CreatePseudoConsole` fails (OS too old, needs Win10 1809+) | `TerminalUnavailableException` → `TerminalPanel` disables "+" with tooltip explaining minimum OS |
| Shell exe missing | `Win32Exception` caught in `TerminalSession` ctor → `ThemedMessageBox` with shell path; no session added |
| Shell exits immediately (< 100ms) | Treated as normal exit — tab closes |
| `InitializeProcThreadAttributeList` / pipe creation failures | Same as `CreatePseudoConsole` failure path |
| Partial init (HPCON created, process launch failed) | Local try/catch in `ConPtyHost.Create` disposes created handles before rethrowing |

### Read loop

- **Never throws into thread pool.** All exceptions caught at top of loop body; synthetic `Exited(-1)` event marshaled to UI thread; session closes as if shell exited.
- `ReadFile` returning 0 bytes or `ERROR_BROKEN_PIPE` = clean EOF path.
- Other Win32 errors = log, close session with `Exited(-1)`.
- **UI-thread drain callback** wraps all dispatcher work in a try/catch. In DEBUG, rethrow so the debugger catches it; in RELEASE, swallow and close the session so a bug in one session doesn't take down the panel.

### VT parser edge cases

Parser never throws on malformed input.

- Unknown escape sequences are swallowed silently; state machine transitions correctly regardless.
- CSI parameter overflow is clamped.
- OSC strings > 64 KB are truncated (defense against `cat /dev/urandom`).
- Invalid UTF-8 → `U+FFFD`.
- Out-of-bounds cursor positions clamped by `TerminalGrid.CellAt`.

### Grid edge cases

- Resize to 0 clamps to 1×1 (pty minimum).
- Alt buffer switch resets scroll position to bottom; main-buffer scroll position restored on exit.
- Scrollback ring wraparound clamps user's scroll position forward if they were in an evicted region.
- Pending-wrap flag per xterm convention: writing the last column sets the flag but leaves cursor there; next character consumes the flag and wraps.

### Keyboard edge cases

- IME composition goes through `OnTextInput` like `EditorControl`, not `OnKeyDown`.
- Paste of large clipboard content chunks at 4 KB, synchronous from UI thread. 100 MB pastes briefly block the UI, which is acceptable for v1.
- **Bracketed paste mode is deferred.** v1 sends raw bytes; multiline paste may execute line-by-line. Matches stock cmd.exe behavior.

### Lifecycle

- **Panel close** disposes all sessions (via `TerminalPanel.OnUnloaded`) — sessions do not outlive the panel.
- **Window close** cascades through panel disposal.
- **Volt crash with running sessions** orphans shell processes. Acceptable for v1 — Windows cleans them up when pipes hit `ERROR_BROKEN_PIPE`. VSCode has the same issue historically.
- **Dispose mid-read:** cancellation flag → close pipes → wait 500ms → kill process → dispose HPCON.

### Theme change

`ThemeManager.ThemeChanged` → `AnsiPalette` re-resolves defaults → all `TerminalView` instances invalidate. Cells store palette indices (not raw colors), so re-rendering with the new palette "just works". Truecolor cells are unaffected by design.

## Testing

### Unit tests (`Volt.Tests/Terminal/`)

**`VtStateMachineTests.cs`** — highest-value. Table-driven: feed bytes, assert event sequence. 80–100 small tests covering every transition in the Williams state chart.

- Ground-state printable bytes → `Print` events (including multi-byte UTF-8 → one `Print`)
- C0 controls → `Execute`
- CSI forms: `ESC [ A`, `ESC [ 3 2 ; 4 5 H`, `ESC [ ; 5 H` (empty param = default), `ESC [ ? 1 0 4 9 h` (intermediates)
- OSC forms: `ESC ] 0 ; title \x07`, `ESC ] 0 ; title \x1b\\` (ST terminator)
- Malformed sequences swallowed without throwing
- Parameter overflow clamped
- Byte-at-a-time feeds produce same output as whole-buffer feeds
- Mid-sequence CAN/SUB cancel, mid-sequence ESC restart

**`VtDispatcherTests.cs`** — dispatcher against a fake `TerminalGrid`. Table-driven by VT sequence:

- Cursor movement, origin, positioning (VT 1-indexed → grid 0-indexed)
- SGR: reset, bold, ANSI fg, xterm-256 fg (`38;5;n`), truecolor fg (`38;2;r;g;b`), combined (`1;31`)
- Alt buffer toggle (`?1049h`/`l`)
- Scroll region (`r`)
- OSC 0/1/2 raises `TitleChanged`

**`TerminalGridTests.cs`** — grid operations in isolation:

- `WriteCell` storage
- Cursor wrap with pending-wrap flag
- `ScrollUp` inside scroll region leaves outside rows untouched
- Main-buffer scrolled-off rows enter scrollback; alt-buffer does not
- `Resize` smaller truncates, larger pads, cursor preserved/clamped
- Scrollback ring wraparound: push `ScrollbackLines + 100`, verify last `ScrollbackLines` survive
- Alt buffer save/restore preserves main buffer exactly
- `EraseInLine`/`EraseInDisplay` modes (to end, to start, all)
- Dirty tracking across mutations; `ClearDirty` resets

**`KeyEncoderTests.cs`** — key → byte sequence:

- Arrows with every modifier combination (plain, Shift, Ctrl, Ctrl+Shift, Alt)
- F1–F4 (SS3 form), F5–F12 (CSI form)
- Home/End/PgUp/PgDn/Ins/Del
- Ctrl+A..Ctrl+Z → `0x01..0x1a`
- Alt+letter → ESC prefix
- Enter → `\r`; Backspace → `\x7f`
- Unmapped keys → `null`

**`AnsiPaletteTests.cs`**:

- Indices 0–15 from theme `terminal.ansi`
- Indices 16–231 = 6×6×6 xterm cube (spot-check known values)
- Indices 232–255 = grayscale ramp endpoints
- Missing `terminal` section → built-in fallback
- `DefaultFg`/`DefaultBg` re-resolve on `ThemeChanged`

### Integration tests (`Volt.Tests/Terminal/Integration/`)

Tagged `[Trait("Category", "Integration")]`; skipped in default `dotnet test`; opt in via `dotnet test --filter Category=Integration`.

- Spawn `cmd.exe /c echo hello`, read output, verify `"hello"` + EOF
- Spawn `cmd.exe`, write `echo test\r\n`, read, verify `"test"`
- Kill mid-session, `Exited` fires with non-zero code within timeout
- Resize mid-session doesn't crash
- Dispose mid-read terminates within 1 second

### Skipped

- `TerminalView` visual rendering tests — WPF visual tests are high-maintenance, and `EditorControl` doesn't have them either. Rendering bugs are easy to diagnose by eye.
- `TerminalPanel` tab-strip interaction — UI plumbing, same reasoning.
- End-to-end "real pty + real WPF window" — too much machinery for the value.

### Benchmarks (`Volt.Benchmarks/Terminal/`)

**`VtParserBenchmarks.cs`** — parser throughput:

- `ParsePlainAscii` — 1 MB plain ASCII (all `Print`), measure ns/byte
- `ParseSgrHeavy` — captured output of colorized `ls`/`tree` (~100 KB), realistic workload
- `ParseAltScreenApp` — captured `vim` redraw

**`TerminalGridBenchmarks.cs`** — grid throughput:

- `WriteCellSequential` — 1M sequential `WriteCell`, throughput
- `ScrollUp1Row` — 80×24 scroll by one row (flood-output hot path)
- `ResizeLarge` — 80×24 → 200×50 (interactive-drag cost)

## Settings

New `AppSettings.Editor` fields:

```csharp
public string? TerminalShellPath { get; set; }        // null = auto-detect on first use
public string? TerminalShellArgs { get; set; }        // null = default args for detected shell
public int TerminalScrollbackLines { get; set; } = 10_000;
```

Theme files gain a `terminal` section (see `AnsiPalette` above).

## Integration with MainWindow

- `MainWindow` constructor registers the `TerminalPanel` via `Shell.RegisterPanel(panel, PanelPlacement.Bottom, defaultSize: 240)`.
- Command palette gains a "Toggle Terminal" command that calls `Shell.TogglePanel("terminal")`.
- Command palette gains "Terminal: New Session" that shows the panel and calls `panel.NewSession()`.
- `` Ctrl+` `` keybinding focuses the terminal panel (or toggles it if already focused).
- `TerminalView`'s shortcut allowlist is extended at startup by `MainWindow` to match the actual keybindings registered for command palette, tab switching, etc., so changes to `MainWindow` shortcuts don't silently drift.

## Risks & Open Questions

- **Read-loop → UI-thread coalescing is the highest-risk performance path.** If the design is wrong, terminal feels sluggish under heavy output. Mitigation: benchmark with real captured output early.
- **The Paul Williams state machine is well-documented but subtle.** Some transitions (e.g., CAN/SUB mid-sequence, ESC re-entry) are easy to get wrong. Mitigation: work row-by-row from the reference diagram and test every transition.
- **Theme integration for terminal colors is a new concept** in the `ColorTheme` model. Adding it touches `ColorTheme.cs`, `ThemeManager.cs`, all shipped theme JSON files, and possibly `ThemeResourceKeys.cs`. Not hard but touches many files.
- **ConPTY minimum OS (Win10 1809, October 2018)** — should be a non-issue in practice but worth surfacing clearly at startup if a user is on an older OS.

## Decisions Deferred to Implementation

The design locks what to build, not every micro-choice. The implementation plan will address:

- Exact cursor-blink rate and whether to use `CompositionTarget.Rendering` vs a `DispatcherTimer`
- Whether `TerminalView` implements `IScrollInfo` directly or hosts a custom scrollbar
- Whether to fold `VtStateMachine` + `VtDispatcher` into one class (probably not — they have different responsibilities and benefit from separate testing)
- Exact glyph-run batching threshold (merge adjacent cells with identical attr, but how many attribute slots to compare)
