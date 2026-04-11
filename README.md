# Volt

A lightweight text editor for Windows, built from scratch with WPF and .NET 10. Volt renders text directly via GlyphRun drawing rather than using built-in WPF text controls, giving full control over rendering, scrolling, and input handling.

## Features

- **Custom rendering engine** -- three-layer DrawingVisual architecture (text, gutter, caret) with GlyphRun-based rendering for sharp ClearType text and smooth scrolling
- **Syntax highlighting** -- regex-based tokenizer with multi-line string state tracking, interpolation/escape highlighting, and background precomputation for large files. Ships with Perl; add languages by dropping a grammar JSON file into `%AppData%/Volt/Grammars/`
- **JSON theming** -- one file controls editor, chrome, and syntax colours. Ships with Default Dark, Default Light, and Gruvbox Dark. Add themes by dropping a JSON file into `%AppData%/Volt/Themes/`
- **Command palette** (Ctrl+Shift+P) -- VS Code-style palette with live preview for theme, font, and editor settings
- **Find and replace** (Ctrl+F) -- match highlighting, case sensitivity toggle, match count, keyboard navigation
- **Smart editing** -- auto-close brackets/quotes, smart indent after `{`/`(`/`[`, tab-stop-aware backspace, double-click word selection
- **Code folding** -- collapse and expand blocks at the gutter or via keyboard shortcuts
- **Word wrap** -- toggleable with word-boundary and indent-preserving modes
- **File explorer** -- side panel with folder/workspace browsing, drag-and-drop file moving, rename, delete with undo support
- **Workspaces** -- multi-root folder workspaces with session persistence (open tabs, caret positions, scroll state)
- **Dockable panels** -- four-region panel system (left, right, top, bottom) with tab strips, resize, and layout persistence
- **Open Recent** -- tracks recently opened files, folders, and workspaces with a searchable command palette view
- **Region-based undo/redo** -- captures only affected lines, scales to large files
- **Customizable keybinds** -- all keyboard shortcuts configurable via Settings
- **Auto-updates** -- checks for updates on startup via GitHub Releases (Velopack)
- **Settings window** -- configurable font family/size/weight, line height, tab size, caret style (bar/block) and blink rate, colour theme
- **Custom window chrome** -- dark mode title bar with DWM integration, themed scrollbars, no white flash on resize

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Build and Run

```bash
dotnet build Volt.sln
dotnet run --project Volt/Volt.csproj
```

## Install

Download the latest installer from [GitHub Releases](https://github.com/willroofe/Volt/releases). The app auto-updates when new versions are published.

Maintainers: see [docs/RELEASE.md](docs/RELEASE.md) for the Velopack build and publish workflow.

## License

[MIT](LICENSE)
