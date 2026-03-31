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

**Single-project solution** with one WPF application project (`TextEdit/`), organized into subdirectories:

- **Editor/** — `EditorControl.cs`, `TextBuffer.cs`, `UndoManager.cs`, `SelectionManager.cs`, `SyntaxManager.cs`, `SyntaxDefinition.cs`
- **Theme/** — `ThemeManager.cs`, `ColorTheme.cs`
- **UI/** — `MainWindow.xaml/.cs`, `CommandPalette.xaml/.cs`, `FindBar.xaml/.cs`, `SettingsWindow.xaml/.cs`
- **Resources/** — `Themes/` (default-dark.json, default-light.json, gruvbox-dark.json), `Grammars/` (perl.json) — embedded resources extracted to `%AppData%/TextEdit/` on first run
- **Root** — `App.xaml/.cs`, `AppSettings.cs`, `AssemblyInfo.cs`

All classes remain in the `TextEdit` namespace.

### EditorControl (`Editor/EditorControl.cs`)

The core of the application — a custom `FrameworkElement` implementing `IScrollInfo`. Everything about editing lives here:

- **Text buffer**: `TextBuffer` class wrapping `List<string>` line-based storage with mutation methods (`InsertAt`, `DeleteAt`, `ReplaceAt`, `JoinWithNext`, `TruncateAt`), dirty tracking, max-line-length caching, and tab expansion
- **Rendering**: Custom layered rendering via `DrawingContext` — no WPF TextBox/RichTextBox. Uses direct `GlyphRun`/`DrawGlyphRun` calls that bypass the expensive DWrite shaping pipeline (since we use monospace fonts with pre-expanded tabs). Three `DrawingVisual` layers: `_textVisual` (editor text), `_gutterVisual` (line numbers), `_caretVisual` (cursor). `OnRender` itself only draws cheap background rectangles (current line, selection, find matches, bracket highlights). Text is clipped to the area right of the gutter so horizontal scrolling doesn't bleed into line numbers.
- **Input**: `OnKeyDown` dispatches to handler methods (`HandleReturn`, `HandleBackspace`, `HandleDelete`, `HandleTab`, `HandleNavigation`, `HandleSelectAll`, `HandleCopy`, `HandleCut`, `HandlePaste`). `OnTextInput` for character input. Mouse handlers for click, drag, double-click word selection.
- **Scrolling**: `IScrollInfo` implementation, hosted inside a `ScrollViewer` with a custom `ThemedScrollViewer` template (defined in `App.xaml`)
- **Undo/redo**: Region-based — each `UndoEntry` stores only the affected line range (before/after lines + caret positions). `BeginEdit`/`EndEdit` pattern captures just the changed region. `UndoManager` manages the stacks with a size cap.
- **Selection**: `SelectionManager` — anchor/caret model with `GetSelectedText`, `DeleteSelection`, `GetOrderedSelection`, and defensive `ClampToBuffer` validation
- **Font**: Configurable font family (monospace only) and size. Instance fields `_monoTypeface`, `_glyphTypeface`, `_fontSize`, `_charWidth`, `_lineHeight`, `_glyphBaseline` — recomputed via `ApplyFont()`. `ApplyFont` obtains a `GlyphTypeface` for direct GlyphRun rendering (graceful fallback: keeps previous GlyphTypeface if the new font doesn't yield one). `GetMonospaceFonts()` discovers system monospace fonts by comparing `i` vs `M` width.
- **Caret**: Supports bar and block styles. Block caret draws the character underneath in `EditorBg` color via GlyphRun. Blink rate configurable (0 = off). `ClampCaret()` helper ensures caret stays within buffer bounds.
- **Colours**: All brush/pen references read from the `ThemeManager` instance property (set by MainWindow). Subscribes to `ThemeManager.ThemeChanged` to `InvalidateVisual()`.
- **Smart editing**: Auto-close brackets/quotes (suppressed inside strings via `IsCaretInsideString()`), backspace deletes both characters of a pair, smart Enter increases indent after `{`/`(`/`[`, smart backspace snaps to tab stops in leading whitespace.
- **Performance**: Syntax tokens cached per line index (`_tokenCache`), pruned to a window around the viewport after each render. Text and gutter visuals use a render buffer (±50 lines beyond viewport) — scroll within the buffer is handled by `TranslateTransform` with no re-render. Background line state precomputation via `Dispatcher.BeginInvoke` at idle priority with generation-based cancellation. DPI cached and updated via `OnDpiChanged`. `UpdateExtent` guards against redundant `ScrollOwner.InvalidateScrollInfo()` calls. `OnMouseMove` early-outs when the caret hasn't moved.
- **Named constants**: `GutterPadding`, `GutterRightMargin`, `GutterSeparatorThickness`, `HorizontalScrollPadding`, `BarCaretWidth`, `DefaultFontSize`, `MouseWheelDeltaUnit`, `ScrollWheelLines`, `RenderBufferLines`, `MaxBracketScanLines`, `PrecomputeBatchSize`
- **Public API**: `SetContent(string)`, `GetContent()`, `MarkClean()`, `InvalidateSyntax()`, properties `IsDirty`/`CaretLine`/`CaretCol`/`TabSize`/`BlockCaret`/`CaretBlinkMs`/`FontFamilyName`/`EditorFontSize`/`EditorFontWeight`, events `DirtyChanged`/`CaretMoved`

### Theming System

Unified JSON-based theming — one theme file controls everything (editor, chrome, syntax). No separate light/dark mode toggle.

**ThemeManager** (`Theme/ThemeManager.cs`) — instance class owned by `App`, accessed via `App.Current.ThemeManager`:

- **Editor colours**: Properties (`EditorBg`, `EditorFg`, `GutterFg`, `CaretBrush`, `SelectionBrush`, `CurrentLineBrush`, `ActiveLineNumberFg`, `MatchingBracketBrush`, `MatchingBracketPen`) read by `EditorControl.OnRender`. Frozen `SolidColorBrush`/`Pen` instances replaced wholesale on theme change.
- **Chrome colours**: Updates keys in `Application.Current.Resources` (prefixed `Theme*`). XAML elements bind via `{DynamicResource ThemeXxx}`.
- **Syntax scope brushes**: `GetScopeBrush(string scope)` returns the brush for a syntax scope, falling back to `EditorFg`.
- **`ThemeChanged` event**: Fired after all colours updated; EditorControl subscribes to trigger re-render.
- **`Apply(string themeName)`**: Loads theme by name from `%AppData%/TextEdit/Themes/`, falls back to "Default Dark".

**ColorTheme** (`Theme/ColorTheme.cs`) — JSON model with three sections:
- `editor`: background, foreground, gutter, caret, selection, current line, bracket matching
- `chrome`: all 17 `DynamicResource` keys (title bar, borders, menus, nav, scrollbars)
- `scopes`: syntax highlighting colours (comment, string, keyword, variable, number, operator, regex, type, function, hashkey)

**Built-in themes**: Default Dark, Default Light, Gruvbox Dark — embedded resource JSON files under `Resources/Themes/`, extracted to `%AppData%/TextEdit/Themes/` on first run via `WriteEmbeddedResource()`.

**Color format**: `#RRGGBB` or `#AARRGGBB` (WPF `ColorConverter` format).

### Syntax Highlighting

**SyntaxManager** (`Editor/SyntaxManager.cs`) — instance class owned by `App`, accessed via `App.Current.SyntaxManager`:
- Loads grammar JSON files from `%AppData%/TextEdit/Grammars/` (default Perl grammar embedded under `Resources/Grammars/`)
- `SetLanguageByExtension(string?)`: auto-detect language from file extension
- `Tokenize(string line, LineState, out LineState)`: returns `List<SyntaxToken>` using first-match-wins regex rules; tracks multi-line string state (unclosed quotes carry across lines)
- Post-processes double-quoted strings for interpolated variables (`$var`, `@array`) and escape sequences (`\n`, `\x1B`, etc.)
- EditorControl maintains a `List<LineState>` with deferred dirty tracking (`_lineStatesDirtyFrom`) and convergence optimization — after an edit, line states are revalidated from the edit point but stop early if the output state matches the cached value

**SyntaxDefinition** (`Editor/SyntaxDefinition.cs`) — grammar model:
- `name`, `extensions` (e.g. `[".pl", ".pm"]`), `rules` (list of pattern + scope)
- Patterns compiled as `Regex` with `RegexOptions.Compiled | Multiline`
- Rule order matters — first match wins for each character position

**Adding a new language**: Drop a JSON file in `%AppData%/TextEdit/Grammars/` with `name`, `extensions`, and `rules`.

**Adding a new theme**: Drop a JSON file in `%AppData%/TextEdit/Themes/` with `name`, `editor`, `chrome`, and `scopes` sections.

### Command Palette (`UI/CommandPalette.xaml` + `.cs`)

XAML + code-behind `UserControl` with two-level navigation:
- **Top level**: list of `PaletteCommand` entries (filterable). Each command is either a `Toggle` (immediate action) or has `GetOptions` (enters sub-list).
- **Sub-level**: list of `PaletteOption` entries with live preview — `ApplyPreview` fires on selection, `Revert` on deselection, `Commit` on Enter. Backspace/Escape returns to top level with revert.
- Hosted in `MainWindow.xaml` as an overlay (`<local:CommandPalette x:Name="CmdPalette" />`).
- Opened via `Ctrl+Shift+P` in `MainWindow.OnKeyDown`.
- Commands defined in `MainWindow.OpenCommandPalette()`: Change Theme, Font Size, Font Family, Font Weight, Tab Size, Toggle Block Caret.

### FindBar (`UI/FindBar.xaml` + `.cs`)

XAML + code-behind `UserControl` — bottom overlay for search:
- Search input with themed case-sensitivity toggle and nav buttons
- Match count display, navigation via Shift+Enter / Enter, close via Escape
- Integrates with EditorControl's find APIs (`SetFindMatches`, `FindNext`, `FindPrevious`, `ClearFindMatches`)
- `RefreshSearch()` re-runs the current query (called after file open/new to update matches for new content)
- Restores search highlights when reopened (calls `UpdateSearch` in `Open()`)
- Shared styles defined in `App.xaml`: `FindBarNavButton`, `FindBarTextButton`, `MatchCaseButton`, `RoundedTextBox`

### MainWindow (`UI/MainWindow.xaml` + `.cs`)

UI shell providing:
- Custom `WindowChrome` title bar with File menu (New/Open/Save/Save As/Settings/Exit)
- Status bar with file type detection and caret position
- Keyboard shortcuts (Ctrl+N/O/S, Ctrl+Shift+S, Ctrl+Shift+P for command palette, Ctrl+F for find)
- Dirty-state tracking with save prompts
- Calls `SyntaxManager.SetLanguageByExtension()` then `Editor.InvalidateSyntax()` when file type changes
- Maximized-window padding compensation via `StateChanged` handler
- Window chrome buttons use Segoe MDL2 Assets icon font
- Window position/size persisted in settings and restored on startup
- **DWM theming**: Uses `DwmSetWindowAttribute` to set `DWMWA_USE_IMMERSIVE_DARK_MODE`, `DWMWA_CAPTION_COLOR`, and `DWMWA_BORDER_COLOR` to match the active theme — eliminates white flash during window resize on dark themes. Applied on `SourceInitialized` and `ThemeManager.ThemeChanged`.

### Settings (`AppSettings.cs` + `UI/SettingsWindow.xaml`)

- JSON persistence at `%AppData%/TextEdit/settings.json`
- Stores: `TabSize` (2/4/8), `BlockCaret`, `CaretBlinkMs` (0-1000), `FontFamily`, `FontSize`, `FontWeight`, `ColorTheme`, window position/size/maximized state
- Settings dialog has two-panel layout: left nav sidebar (Behaviour/Appearance) with custom `WindowChrome` title bar
- All controls in SettingsWindow are custom-themed (ComboBox, Slider, Buttons) — no default Windows styles
- `SettingsWindow` receives `ThemeManager` via constructor parameter

### App.xaml / App.xaml.cs

- **Owns** `ThemeManager` and `SyntaxManager` as instance properties, accessed via typed `App.Current` accessor
- **Startup order** (`OnStartup`): `ThemeManager.Initialize()` → `SyntaxManager.Initialize()` → `AppSettings.Load()` → `ThemeManager.Apply()` → async monospace font cache warm-up at idle priority
- **App.xaml** defines: default light-theme `SolidColorBrush` resources (overwritten at runtime by `ThemeManager`), custom `ScrollBar` style with themed thumb/track (slim modern style), `ThemedScrollViewer` control template, shared FindBar/CommandPalette styles

## Key Design Rules

- **All XAML colours use `{DynamicResource}`** — never hardcode colours in XAML (except close-button red `#E81123` which is theme-invariant). New colour keys must be added to: the `ChromeColors` class in `ColorTheme.cs`, `ThemeManager.UpdateAppResources()`, `App.xaml` defaults, and the default theme JSON files in `Resources/Themes/`.
- **Menu styling caveat**: Top-level File MenuItem uses a custom `ControlTemplate` (`TopLevelMenuItem` style). This is safe because File is the only top-level item — do NOT add custom ControlTemplates to top-level MenuItems if there are multiple, as it breaks WPF's built-in hover-to-switch behavior.
- **Dead zone overlay** (6px) at bottom of menu bar prevents accidental hover on adjacent menu items when moving toward dropdown.
- **EditorControl never caches brushes locally** — always reads from the `ThemeManager` instance property so theme changes take effect immediately.
- **Grammar rule order matters** — rules are applied in definition order, first match wins. Strings should come before comments (so `#` inside strings isn't treated as a comment).
- **Syntax tokenization uses `LineState` for multi-line strings** — unclosed quotes carry state across lines via `EnsureLineStates()`. Other token types (comments, etc.) are per-line only. String interpolation and escape sequences are handled as a post-processing step on double-quoted string tokens.
- **Default resource files in `%AppData%` are only written if absent** — if you change an embedded resource JSON file, users must delete the old file in `%AppData%` for changes to take effect.
- **GlyphRun rendering**: All text drawing uses `DrawGlyphRun` (not `FormattedText`/`DrawText`). This bypasses DWrite's script analysis, itemization, and shaping — critical for scroll performance. The `DrawGlyphRun` helper maps characters to glyph indices via `GlyphTypeface.CharacterToGlyphMap`, sets uniform advance widths, and pixel-snaps the origin. When drawing syntax-highlighted lines, gaps between tokens must be filled with `EditorFg` (tokens don't cover the full line).
- **Performance-sensitive code paths**: `OnRender`, `OnKeyDown`/`OnTextInput`, `OnMouseMove` during drag, and `UpdateExtent` are called frequently. Never use `FormattedText` in rendering loops (use `DrawGlyphRun`). Avoid unconditional `InvalidateVisual()` or `ScrollOwner.InvalidateScrollInfo()` calls, and always guard with change-detection before triggering layout/render cycles.
- **Text buffer mutations**: Always use `TextBuffer` methods (`InsertAt`, `DeleteAt`, `ReplaceAt`, `JoinWithNext`, `TruncateAt`) rather than inline string slicing — they handle max-line-length cache invalidation internally.
- **Defensive bounds clamping**: `ClampCaret()` in EditorControl and `ClampToBuffer()` in SelectionManager ensure positions are valid before indexing. Call after undo/redo restores and before bracket matching.
