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
- **Undo/redo**: Full-snapshot-based (captures entire line buffer per operation) via `UndoEntry` record
- **Selection**: Anchor/caret model with shift-click and drag support
- **Font**: Configurable font family (monospace only) and size. Instance fields `_monoTypeface`, `_fontSize`, `_charWidth`, `_lineHeight` — recomputed via `ApplyFont()`. `GetMonospaceFonts()` discovers system monospace fonts by comparing `i` vs `M` width.
- **Caret**: Supports bar (1px) and block (full character width) styles. Block caret draws the character underneath in `EditorBg` color. Blink rate configurable (0 = off).
- **Colours**: All brush/pen references read from `ThemeManager` static properties (not local fields). Subscribes to `ThemeManager.ThemeChanged` to `InvalidateVisual()`.
- **Smart editing**: Auto-close brackets/quotes (suppressed inside strings via `IsCaretInsideString()`), backspace deletes both characters of a pair, smart Enter increases indent after `{`/`(`/`[`, smart backspace snaps to tab stops in leading whitespace.
- **Public API**: `SetContent(string)`, `GetContent()`, `MarkClean()`, properties `IsDirty`/`CaretLine`/`CaretCol`/`TabSize`/`BlockCaret`/`CaretBlinkMs`/`FontFamilyName`/`EditorFontSize`, events `DirtyChanged`/`CaretMoved`

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
- `Tokenize(string line)`: returns `List<SyntaxToken>` using first-match-wins regex rules
- Post-processes double-quoted strings for interpolated variables (`$var`, `@array`)
- Ships a default Perl grammar (`perl.json`)

**SyntaxDefinition** (`TextEdit/SyntaxDefinition.cs`) — grammar model:
- `name`, `extensions` (e.g. `[".pl", ".pm"]`), `rules` (list of pattern + scope)
- Patterns compiled as `Regex` with `RegexOptions.Compiled | Multiline`
- Rule order matters — first match wins for each character position

**Adding a new language**: Drop a JSON file in `%AppData%/TextEdit/Grammars/` with `name`, `extensions`, and `rules`.

**Adding a new theme**: Drop a JSON file in `%AppData%/TextEdit/Themes/` with `name`, `editor`, `chrome`, and `scopes` sections.

### MainWindow (`TextEdit/MainWindow.xaml` + `.cs`)

UI shell providing:
- Custom `WindowChrome` title bar with File menu (New/Open/Save/Save As/Settings)
- Status bar with file type detection and caret position
- Keyboard shortcuts (Ctrl+N/O/S, Ctrl+Shift+S)
- Dirty-state tracking with save prompts
- Calls `SyntaxManager.SetLanguageByExtension()` when file type changes
- Maximized-window padding compensation via `StateChanged` handler
- Window chrome buttons use Segoe MDL2 Assets icon font

### Settings (`TextEdit/AppSettings.cs` + `SettingsWindow.xaml`)

- JSON persistence at `%AppData%/TextEdit/settings.json`
- Stores: `TabSize` (2/4/8), `BlockCaret`, `CaretBlinkMs` (0-1000), `FontFamily`, `FontSize`, `ColorTheme`
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
- **Syntax tokenization is per-line** — no multi-line token support. String interpolation is handled as a post-processing step on double-quoted string tokens.
- **Default theme files in `%AppData%` are only written if absent** — if you change an embedded default theme string in `ThemeManager.cs`, users must delete the old file for changes to take effect.
