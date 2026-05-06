# Repository Guidelines

## Project Structure & Module Organization

Volt is a Windows text editor built with WPF on .NET 10. The main application lives in `Volt/`; editor logic is under `Volt/Editor`, UI and XAML views under `Volt/UI`, terminal support under `Volt/Terminal`, themes, grammars, fonts, and icons under `Volt/Resources`, and shared app services at the project root. Tests live in `Volt.Tests/`, with terminal-specific coverage in `Volt.Tests/Terminal`. Performance benchmarks live in `Volt.Benchmarks/`.

Documentation lives in `docs/`. Use `docs/RELEASE.md` for the Velopack and GitHub release workflow, keep reusable release-note bodies under `docs/release-notes/`, and keep grammar samples under `docs/grammar-test-files/`.

## Build, Test, and Development Commands

- `dotnet restore Volt.sln` restores NuGet packages.
- `dotnet build Volt.sln` builds the app, tests, and benchmarks.
- `dotnet run --project Volt/Volt.csproj` runs the editor locally on Windows.
- `dotnet test Volt.Tests/Volt.Tests.csproj` runs the xUnit test suite.
- `dotnet run -c Release --project Volt.Benchmarks/Volt.Benchmarks.csproj` runs BenchmarkDotNet benchmarks.

For releases, follow `docs/RELEASE.md`. Velopack package versions must be three-part SemVer values such as `1.5.0`, and `vpk pack` must receive `--releaseNotes docs/release-notes/v<VERSION>.md` so the in-app update dialog can display notes.

## Coding Style & Naming Conventions

Follow `.editorconfig`: spaces, 4-space indentation for C#, CRLF line endings, UTF-8, trimmed trailing whitespace, and final newlines. Markdown keeps trailing whitespace untouched. XAML, XML, and JSON use 2-space indentation. Prefer file-scoped namespaces, explicit built-in types over `var`, `var` when the type is apparent, expression-bodied properties, and braces for multiline blocks. Use PascalCase for public types and members, camelCase for locals and parameters, and keep test class names aligned with the unit under test, such as `TextBufferTests`.

## Testing Guidelines

Tests use xUnit with `Microsoft.NET.Test.Sdk` and `Xunit.StaFact` for WPF/STA-sensitive cases. Add or update tests in `Volt.Tests` near the behavior being changed; terminal tests should stay under `Volt.Tests/Terminal`. Name tests after observable behavior, and prefer focused unit tests for editor buffers, layout, parsing, command state, updates, and terminal behavior. Run `dotnet test Volt.Tests/Volt.Tests.csproj` before submitting code changes. For docs-only changes, a test run is usually unnecessary unless the docs change release commands or other executable examples.

## Commit & Pull Request Guidelines

Recent history uses short imperative subjects, sometimes scoped, for example `docs(release): embed Velopack release notes`, `docs: remove Claude instructions`, or `Editor split drag: half-pane drop`. Keep commits focused and describe the user-visible or technical effect. Pull requests should include a concise summary, test results, linked issues when applicable, and screenshots or recordings for visible UI changes.

`master` is the default/release branch and `develop` is the integration branch. When merging `develop` into `master` for a release, push the merge commit before creating the GitHub release, then return to the branch requested by the task.

## Security & Configuration Tips

Do not commit local IDE settings, generated build output, release artifacts, benchmark output, or user-specific configuration. Ignored/generated locations include `.idea/`, `.claude/`, `.superpowers/`, `bin/`, `obj/`, `publish/`, `Releases/`, `BenchmarkDotNet.Artifacts/`, and `test-files/`.

User-installed grammars and themes belong under `%AppData%/Volt/Grammars/` and `%AppData%/Volt/Themes/`, not in source unless they are intended bundled resources. Bundled grammar and theme changes should go under `Volt/Resources/Grammars/` and `Volt/Resources/Themes/`, with tests or samples when behavior changes.
