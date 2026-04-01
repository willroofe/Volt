# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TextEdit is a custom text editor built as a WPF desktop application targeting .NET 10 on Windows. It features a from-scratch editor control (`EditorControl`) that renders text directly via WPF's `DrawingContext` rather than using built-in WPF text controls.

## Build & Run

```bash
dotnet build TextEdit.sln
dotnet run --project TextEdit/TextEdit.csproj
```

No test framework is configured. The running app must be closed before rebuilding (the exe gets locked).

**Benchmarks** (`TextEdit.Benchmarks/` — BenchmarkDotNet, InProcess toolchain for .NET 10 + WPF):

```bash
dotnet run -c Release --project TextEdit.Benchmarks              # all benchmarks
dotnet run -c Release --project TextEdit.Benchmarks -- --filter *Tokenize*   # specific suite
dotnet run -c Release --project TextEdit.Benchmarks -- --list flat            # list available
```

## Architecture

**Single-project solution** with one WPF application project (`TextEdit/`), organized into subdirectories:

- **Editor/** — `EditorControl.cs`, `TextBuffer.cs`, `UndoManager.cs`, `SelectionManager.cs`, `FontManager.cs`, `FindManager.cs`, `BracketMatcher.cs`, `SyntaxManager.cs`, `SyntaxDefinition.cs`
- **Theme/** — `ThemeManager.cs`, `ColorTheme.cs`
- **UI/** — `MainWindow.xaml/.cs`, `CommandPalette.xaml/.cs`, `FindBar.xaml/.cs`, `SettingsWindow.xaml/.cs` (uses `SettingsSnapshot` record), `TabInfo.cs`, `DwmHelper.cs`, `FileHelper.cs`, `CommandPaletteCommands.cs`
- **Resources/** — `Themes/` (default-dark.json, default-light.json, gruvbox-dark.json), `Grammars/` (perl.json) — embedded resources extracted to `%AppData%/TextEdit/` on first run
- **Root** — `App.xaml/.cs`, `AppSettings.cs`, `AssemblyInfo.cs`

All classes remain in the `TextEdit` namespace.

### EditorControl (`Editor/EditorControl.cs`)

The core of the application (~1,700 lines) — a custom `FrameworkElement` implementing `IScrollInfo`. Delegates to extracted helper classes for font metrics/glyph rendering (`FontManager`), find/replace state (`FindManager`), and bracket matching (`BracketMatcher`).

- **Rendering**: Three `DrawingVisual` layers (`_textVisual`, `_gutterVisual`, `_caretVisual`). `OnRender` draws background rectangles (current line, selection, find matches, bracket highlights). Text rendering uses `FontManager.DrawGlyphRun` — direct `GlyphRun` calls bypassing DWrite shaping. Text clipped to the area right of the gutter.
- **Input**: `OnKeyDown` dispatches to `Handle*` methods. `OnTextInput` for character input. Mouse handlers for click, drag, double-click word selection.
- **Scrolling**: `IScrollInfo` implementation with render buffer (±50 lines beyond viewport) — scroll within the buffer uses `TranslateTransform` with no re-render.
- **Undo/redo**: Two entry types in `UndoManager`: `UndoEntry` (region-based, stores affected line range before/after) for general edits via `BeginEdit`/`EndEdit`, and `IndentEntry` (compact, stores only `int[]` of spaces-per-line) for multi-line indent/unindent. Edit handlers use shared helpers `GetEditRange()`, `DeleteSelectionIfPresent()`, and `FinishEdit()` for consistent pre/post boilerplate. `Undo()`/`Redo()` in EditorControl dispatch on entry type with a defensive `else throw` for unknown types.
- **Smart editing**: Auto-close brackets/quotes (suppressed inside strings), smart indent after openers, tab-stop-aware backspace. Bracket pair data lives in `BracketMatcher`.
- **Performance**: Syntax token cache pruned to viewport window (reusable `_pruneKeys` list avoids per-render allocation). Background line state precomputation at idle priority with generation-based cancellation. `UpdateExtent` guards against redundant `InvalidateScrollInfo()`. Find match rendering uses binary search for O(log n + visible) instead of scanning all matches.
- **Memory**: `ReleaseResources()` clears undo, buffer, and caches with `TrimExcess()` on tab close. `CloseTab` triggers forced compacting GC for large files to return memory to the OS.

### Theming System

Unified JSON-based theming — one theme file controls everything (editor, chrome, syntax). No separate light/dark mode toggle.

**ThemeManager** (`Theme/ThemeManager.cs`) — instance class owned by `App`, accessed via `App.Current.ThemeManager`:
- Editor colour properties (`EditorBg`, `EditorFg`, `CaretBrush`, etc.) — frozen `SolidColorBrush`/`Pen` instances read directly by `EditorControl.OnRender`
- Chrome colours — updates `Application.Current.Resources` keys (prefixed `Theme*`). XAML binds via `{DynamicResource ThemeXxx}`
- `GetScopeBrush(string scope)` — syntax highlighting brush lookup, falling back to `EditorFg`
- `ThemeChanged` event — EditorControl subscribes to trigger re-render
- `Apply(string themeName)` — loads from `%AppData%/TextEdit/Themes/`, falls back to "Default Dark"

**DwmHelper** (`UI/DwmHelper.cs`) — applies `DWMWA_USE_IMMERSIVE_DARK_MODE`, `DWMWA_CAPTION_COLOR`, and `DWMWA_BORDER_COLOR` to match the active theme. Called from MainWindow on `SourceInitialized` and `ThemeChanged`.

**ColorTheme** (`Theme/ColorTheme.cs`) — JSON model with `editor`, `chrome` (17 `DynamicResource` keys), and `scopes` sections. Color format: `#RRGGBB` or `#AARRGGBB`.

**Adding a new theme**: Drop a JSON file in `%AppData%/TextEdit/Themes/` with `name`, `editor`, `chrome`, and `scopes` sections.

### Syntax Highlighting

**SyntaxManager** (`Editor/SyntaxManager.cs`) — instance class owned by `App`, accessed via `App.Current.SyntaxManager`:
- Loads grammar JSON from `%AppData%/TextEdit/Grammars/`
- `Tokenize(line, LineState, out LineState)` — orchestrator that delegates to phase-specific private methods (block comments, heredocs, regex continuation, string continuation, grammar rules, post-rule detection). First-match-wins regex rules with multi-line string state tracking.
- Post-processes double-quoted strings for interpolated variables and escape sequences via `ExpandInterpolation`
- EditorControl maintains `List<LineState>` with convergence optimization — revalidation stops early when output state matches cached value

**Adding a new language**: Drop a JSON file in `%AppData%/TextEdit/Grammars/` with `name`, `extensions`, and `rules`.

### MainWindow (`UI/MainWindow.xaml` + `.cs`)

UI shell (~840 lines) with tab management, file I/O, settings, and keyboard shortcuts. Delegates to:
- **FileHelper** (`UI/FileHelper.cs`) — `AtomicWriteText`, `DetectEncoding`, file type name lookup
- **CommandPaletteCommands** (`UI/CommandPaletteCommands.cs`) — builds command list with preview/commit/revert lambdas
- **DwmHelper** (`UI/DwmHelper.cs`) — DWM window attribute management

### App.xaml / App.xaml.cs

- **Owns** `ThemeManager` and `SyntaxManager` as instance properties, accessed via typed `App.Current` accessor
- **Startup order**: `ThemeManager.Initialize()` → `SyntaxManager.Initialize()` → `AppSettings.Load()` → `ThemeManager.Apply()` → async monospace font cache warm-up at idle priority
- **App.xaml** defines: default `SolidColorBrush` resources (overwritten by `ThemeManager`), custom `ScrollBar`/`ThemedScrollViewer` styles, `TabCloseButton` style, shared FindBar/CommandPalette styles

## Key Design Rules

- **All XAML colours use `{DynamicResource}`** — never hardcode colours in XAML (except close-button red `#E81123` which is theme-invariant). New colour keys must be added to: `ChromeColors` in `ColorTheme.cs`, `ThemeManager.UpdateAppResources()`, `App.xaml` defaults, and the default theme JSON files.
- **Menu styling caveat**: Top-level File MenuItem uses a custom `ControlTemplate` (`TopLevelMenuItem` style). This is safe because File is the only top-level item — do NOT add custom ControlTemplates to top-level MenuItems if there are multiple, as it breaks WPF's built-in hover-to-switch behavior.
- **Dead zone overlay** (6px) at bottom of menu bar prevents accidental hover on adjacent menu items when moving toward dropdown.
- **EditorControl never caches brushes locally** — always reads from the `ThemeManager` instance property so theme changes take effect immediately.
- **Grammar rule order matters** — rules are applied in definition order, first match wins. Strings should come before comments (so `#` inside strings isn't treated as a comment).
- **Syntax tokenization uses `LineState` for multi-line constructs** — tracks unclosed quotes, block comments, heredocs, and open regex delimiters across lines via `EnsureLineStates()`. `ContinueOpenRegex` must mark `claimed[]` on continuation lines to prevent post-rule detection from corrupting the open regex state. String interpolation and escape sequences are post-processed on double-quoted string tokens.
- **Built-in resource files are always overwritten on startup** — themes and grammars shipped as embedded resources are re-extracted to `%AppData%/TextEdit/` each launch so embedded fixes take effect. User-created files (not matching built-in names) are left untouched.
- **GlyphRun rendering**: All text drawing uses `FontManager.DrawGlyphRun` (not `FormattedText`/`DrawText`). This bypasses DWrite shaping — critical for scroll performance. Advance widths are cached in a shared `_uniformAdvanceWidths` array (all values identical for monospace) passed via `ArraySegment<double>` to avoid per-call allocation. When drawing syntax-highlighted lines, gaps between tokens must be filled with `EditorFg` (tokens don't cover the full line).
- **Performance-sensitive code paths**: `OnRender`, `OnKeyDown`/`OnTextInput`, `OnMouseMove` during drag, and `UpdateExtent` are hot paths. Never use `FormattedText` in rendering loops. Avoid unconditional `InvalidateVisual()` or `ScrollOwner.InvalidateScrollInfo()` — always guard with change-detection.
- **Text buffer mutations**: Always use `TextBuffer` methods (`InsertAt`, `DeleteAt`, `ReplaceAt`, `JoinWithNext`, `TruncateAt`) — they handle max-line-length cache invalidation internally. These use `string.Concat` with `AsSpan` to avoid intermediate string allocations.
- **Defensive bounds clamping**: `ClampCaret()` in EditorControl and `ClampToBuffer()` in SelectionManager ensure positions are valid before indexing. Call after undo/redo restores and before bracket matching.
