# TextEdit

A lightweight text editor for Windows, built from scratch with WPF and .NET 10. TextEdit renders text directly via GlyphRun drawing rather than using built-in WPF text controls, giving full control over rendering, scrolling, and input handling.

## Features

- **Custom rendering engine** -- three-layer DrawingVisual architecture (text, gutter, caret) with GlyphRun-based rendering for sharp ClearType text and smooth scrolling
- **Syntax highlighting** -- regex-based tokenizer with multi-line string state tracking, interpolation/escape highlighting, and background precomputation for large files. Ships with Perl; add languages by dropping a grammar JSON file into `%AppData%/TextEdit/Grammars/`
- **JSON theming** -- one file controls editor, chrome, and syntax colours. Ships with Default Dark, Default Light, and Gruvbox Dark. Add themes by dropping a JSON file into `%AppData%/TextEdit/Themes/`
- **Command palette** (Ctrl+Shift+P) -- VS Code-style palette with live preview for theme, font, and editor settings
- **Find and replace** (Ctrl+F) -- match highlighting, case sensitivity toggle, match count, keyboard navigation
- **Smart editing** -- auto-close brackets/quotes, smart indent after `{`/`(`/`[`, tab-stop-aware backspace, double-click word selection
- **Region-based undo/redo** -- captures only affected lines, scales to large files
- **Settings window** -- configurable font family/size/weight, tab size, caret style (bar/block) and blink rate, colour theme
- **Custom window chrome** -- dark mode title bar with DWM integration, themed scrollbars, no white flash on resize

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Build and Run

```bash
dotnet build TextEdit.sln
dotnet run --project TextEdit/TextEdit.csproj
```

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+N | New file |
| Ctrl+O | Open file |
| Ctrl+S | Save |
| Ctrl+Shift+S | Save As |
| Ctrl+F | Find / Replace |
| Ctrl+Shift+P | Command palette |
| Ctrl+Z | Undo |
| Ctrl+Y | Redo |
| Ctrl+A | Select all |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / Cut / Paste |
| Ctrl+Plus / Ctrl+Minus | Increase / Decrease font size |

## Project Structure

```
TextEdit/
  Editor/       Core editor: EditorControl, TextBuffer, UndoManager, SelectionManager, SyntaxManager
  Theme/        ThemeManager, ColorTheme model
  UI/           MainWindow, FindBar, CommandPalette, SettingsWindow
  Resources/    Embedded default themes and grammars (JSON)
```

## License

This project is not currently published under a specific license.
