# Volt

Volt is a lightweight text editor for Windows, built with WPF and .NET 10. It focuses on feeling fast, clean, and comfortable for everyday editing while still offering the essentials you expect from a code editor.

## Highlights

- Fast custom text rendering with smooth scrolling, line gutters, selections, and caret handling.
- Syntax highlighting for common programming, markup, config, and scripting languages.
- Built-in themes with JSON-based custom themes.
- Command palette, find and replace, configurable shortcuts, and editor settings.
- Smart editing conveniences such as bracket pairing, indentation, word wrap, folding, and undo/redo.
- Workspace support with tabs, recent files, a file explorer, and persistent layout state.

Custom grammars can be placed in `%AppData%/Volt/Grammars/`, and custom themes in `%AppData%/Volt/Themes/`.

## Install

Download the latest installer from [GitHub Releases](https://github.com/willroofe/Volt/releases).

## Build From Source

Requirements:

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```bash
dotnet restore Volt.sln
dotnet build Volt.sln
dotnet run --project Volt/Volt.csproj
```

## Contributing

Tests live in `Volt.Tests/` and can be run with:

```bash
dotnet test Volt.Tests/Volt.Tests.csproj
```

Maintainers can find the Velopack packaging and publishing workflow in [docs/RELEASE.md](docs/RELEASE.md).

## License

[MIT](LICENSE)
