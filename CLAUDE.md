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

## Architecture

**Single-project solution** with one WPF application project (`TextEdit/`).

### EditorControl (`TextEdit/EditorControl.cs`)

The core of the application — a custom `FrameworkElement` implementing `IScrollInfo`. Everything about editing lives here:

- **Text buffer**: `List<string>` line-based storage
- **Rendering**: Custom `OnRender` using `FormattedText` and `DrawingContext` — no WPF TextBox/RichTextBox. Only visible lines are drawn (viewport-culled). Text is clipped to the area right of the gutter so horizontal scrolling doesn't bleed into line numbers. Syntax tokens are applied per-line via `FormattedText.SetForegroundBrush()`.
- **Input**: `OnKeyDown`/`OnTextInput` for keyboard; mouse handlers for click, drag, double-click word selection
- **Scrolling**: `IScrollInfo` implementation, hosted inside a `ScrollViewer` with a custom `ThemedScrollViewer` template (defined in `App.xaml`)
- **Undo/redo**: Full-snapshot-based (copies entire `List<string>` per operation) via `UndoEntry` record
- **Selection**: Anchor/caret model with shift-click and drag support
- **Font**: Configurable font family (monospace only) and size. Instance fields `_monoTypeface`, `_fontSize`, `_charWidth`, `_lineHeight` — recomputed via `ApplyFont()`. `GetMonospaceFonts()` discovers system monospace fonts by comparing `i` vs `M` width.
- **Caret**: Supports bar (1px) and block (full character width) styles. Block caret draws the character underneath in `EditorBg` color. Blink rate configurable (0 = off).
- **Colours**: All brush/pen references read from `ThemeManager` static properties (not local fields). Subscribes to `ThemeManager.ThemeChanged` to `InvalidateVisual()`.
- **Smart editing**: Auto-close brackets/quotes (suppressed inside strings via `IsCaretInsideString()`), backspace deletes both characters of a pair, smart Enter increases indent after `{`/`(`/`[`, smart backspace snaps to tab stops in leading whitespace.
- **Performance**: `FormattedText` objects and line-number texts are cached per line index (`_ftCache`, `_lineNumCache`), pruned to a window around the viewport after each render. DPI is cached and updated via `OnDpiChanged`. `UpdateExtent` guards against redundant `ScrollOwner.InvalidateScrollInfo()` calls. `OnMouseMove` early-outs when the caret hasn't moved to avoid redundant renders during drag.
- **Public API**: `SetContent(string)`, `GetContent()`, `MarkClean()`, `InvalidateSyntax()`, properties `IsDirty`/`CaretLine`/`CaretCol`/`TabSize`/`BlockCaret`/`CaretBlinkMs`/`FontFamilyName`/`EditorFontSize`/`EditorFontWeight`, events `DirtyChanged`/`CaretMoved`

### Theming System

Unified JSON-based theming — one theme file controls everything (editor, chrome, syntax). No separate light/dark mode toggle.

**ThemeManager** (`TextEdit/ThemeManager.cs`) — static class providing:

- **Editor colours**: Static properties (`EditorBg`, `EditorFg`, `GutterFg`, `CaretBrush`, `SelectionBrush`, `CurrentLineBrush`, `ActiveLineNumberFg`, `MatchingBracketBrush`, `MatchingBracketPen`) read directly by `EditorControl.OnRender`. Frozen `SolidColorBrush`/`Pen` instances replaced wholesale on theme change.
- **Chrome colours**: Updates keys in `Application.Current.Resources` (prefixed `Theme*`). XAML elements bind via `{DynamicResource ThemeXxx}`.
- **Syntax scope brushes**: `GetScopeBrush(string scope)` returns the brush for a syntax scope, falling back to `EditorFg`.
- **`ThemeChanged` event**: Fired after all colours updated; EditorControl subscribes to trigger re-render.
- **`Apply(string themeName)`**: Loads theme by name from `%AppData%/TextEdit/Themes/`, falls back to "Default Dark".

**ColorTheme** (`TextEdit/ColorTheme.cs`) — JSON model with three sections:
- `editor`: background, foreground, gutter, caret, selection, current line, bracket matching
- `chrome`: all 17 `DynamicResource` keys (title bar, borders, menus, nav, scrollbars)
- `scopes`: syntax highlighting colours (comment, string, keyword, variable, number, operator, regex, type, function, hashkey)

**Built-in themes**: Default Dark, Default Light, Gruvbox Dark (embedded as string literals in `ThemeManager.cs`, written to `%AppData%/TextEdit/Themes/` on first run).

**Color format**: `#RRGGBB` or `#AARRGGBB` (WPF `ColorConverter` format).

### Syntax Highlighting

**SyntaxManager** (`TextEdit/SyntaxManager.cs`) — static class:
- Loads grammar JSON files from `%AppData%/TextEdit/Grammars/`
- `SetLanguageByExtension(string?)`: auto-detect language from file extension
- `Tokenize(string line, LineState, out LineState)`: returns `List<SyntaxToken>` using first-match-wins regex rules; tracks multi-line string state (unclosed quotes carry across lines)
- Post-processes double-quoted strings for interpolated variables (`$var`, `@array`) and escape sequences (`\n`, `\x1B`, etc.)
- Ships a default Perl grammar (`perl.json`)
- EditorControl maintains a `List<LineState>` with deferred dirty tracking (`_lineStatesDirtyFrom`) and convergence optimization — after an edit, line states are revalidated from the edit point but stop early if the output state matches the cached value

**SyntaxDefinition** (`TextEdit/SyntaxDefinition.cs`) — grammar model:
- `name`, `extensions` (e.g. `[".pl", ".pm"]`), `rules` (list of pattern + scope)
- Patterns compiled as `Regex` with `RegexOptions.Compiled | Multiline`
- Rule order matters — first match wins for each character position

**Adding a new language**: Drop a JSON file in `%AppData%/TextEdit/Grammars/` with `name`, `extensions`, and `rules`.

**Adding a new theme**: Drop a JSON file in `%AppData%/TextEdit/Themes/` with `name`, `editor`, `chrome`, and `scopes` sections.

### Command Palette (`TextEdit/CommandPalette.cs`)

Code-behind-only `UserControl` (no XAML) — builds UI in constructor. Supports two-level navigation:
- **Top level**: list of `PaletteCommand` entries (filterable). Each command is either a `Toggle` (immediate action) or has `GetOptions` (enters sub-list).
- **Sub-level**: list of `PaletteOption` entries with live preview — `ApplyPreview` fires on selection, `Revert` on deselection, `Commit` on Enter. Backspace/Escape returns to top level with revert.
- Hosted in `MainWindow.xaml` as an overlay (`<local:CommandPalette x:Name="CmdPalette" />`).
- Opened via `Ctrl+Shift+P` in `MainWindow.OnKeyDown`.
- Commands defined in `MainWindow.OpenCommandPalette()`: Change Theme, Font Size, Font Family, Font Weight, Tab Size, Toggle Block Caret.

### MainWindow (`TextEdit/MainWindow.xaml` + `.cs`)

UI shell providing:
- Custom `WindowChrome` title bar with File menu (New/Open/Save/Save As/Settings)
- Status bar with file type detection and caret position
- Keyboard shortcuts (Ctrl+N/O/S, Ctrl+Shift+S, Ctrl+Shift+P for command palette)
- Dirty-state tracking with save prompts
- Calls `SyntaxManager.SetLanguageByExtension()` then `Editor.InvalidateSyntax()` when file type changes
- Maximized-window padding compensation via `StateChanged` handler
- Window chrome buttons use Segoe MDL2 Assets icon font
- Window position/size persisted in settings and restored on startup

### Settings (`TextEdit/AppSettings.cs` + `SettingsWindow.xaml`)

- JSON persistence at `%AppData%/TextEdit/settings.json`
- Stores: `TabSize` (2/4/8), `BlockCaret`, `CaretBlinkMs` (0-1000), `FontFamily`, `FontSize`, `FontWeight`, `ColorTheme`, window position/size/maximized state
- Settings dialog has two-panel layout: left nav sidebar (Behaviour/Appearance) with custom `WindowChrome` title bar
- All controls in SettingsWindow are custom-themed (ComboBox, Slider, Buttons) — no default Windows styles
- Theme applied on startup in `App.xaml.cs` via `ThemeManager.Apply()`

### App.xaml

Defines:
- Default light-theme `SolidColorBrush` resources (overwritten at runtime by `ThemeManager`)
- Custom `ScrollBar` style with themed thumb/track (no arrow buttons, slim modern style)
- `ThemedScrollViewer` control template that themes the corner rectangle where scrollbars meet

## Key Design Rules

- **All XAML colours use `{DynamicResource}`** — never hardcode colours in XAML (except close-button red `#E81123` which is theme-invariant). New colour keys must be added to: the `ChromeColors` class in `ColorTheme.cs`, `ThemeManager.UpdateAppResources()`, `App.xaml` defaults, and both default theme JSON strings.
- **Menu styling caveat**: Top-level File MenuItem uses a custom `ControlTemplate` (`TopLevelMenuItem` style). This is safe because File is the only top-level item — do NOT add custom ControlTemplates to top-level MenuItems if there are multiple, as it breaks WPF's built-in hover-to-switch behavior.
- **Dead zone overlay** (6px) at bottom of menu bar prevents accidental hover on adjacent menu items when moving toward dropdown.
- **EditorControl never caches brushes locally** — always reads from `ThemeManager` static properties so theme changes take effect immediately.
- **Grammar rule order matters** — rules are applied in definition order, first match wins. Strings should come before comments (so `#` inside strings isn't treated as a comment).
- **Syntax tokenization uses `LineState` for multi-line strings** — unclosed quotes carry state across lines via `EnsureLineStates()`. Other token types (comments, etc.) are per-line only. String interpolation and escape sequences are handled as a post-processing step on double-quoted string tokens.
- **Default theme files in `%AppData%` are only written if absent** — if you change an embedded default theme string in `ThemeManager.cs`, users must delete the old file for changes to take effect.
- **Startup order** (`App.OnStartup`): `ThemeManager.Initialize()` → `SyntaxManager.Initialize()` → `AppSettings.Load()` → `ThemeManager.Apply()` → async monospace font cache warm-up at idle priority.
- **Performance-sensitive code paths**: `OnRender`, `OnKeyDown`/`OnTextInput`, `OnMouseMove` during drag, and `UpdateExtent` are called frequently. Avoid allocating `FormattedText` outside of rendering, avoid unconditional `InvalidateVisual()` or `ScrollOwner.InvalidateScrollInfo()` calls, and always guard with change-detection before triggering layout/render cycles.
