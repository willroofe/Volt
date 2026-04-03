# Code Quality Review — Volt Text Editor

**Date:** 2026-04-03
**Scope:** Full project — every `.cs`, `.xaml`, `.xaml.cs`, config, and resource file reviewed.
**Remediation:** All minor and moderate findings resolved. Critical findings remain for future work.

---

## Executive Summary

Volt is a well-structured, focused WPF text editor with ~10,000 lines of code across 40+ source files. The codebase demonstrates strong performance awareness (GlyphRun rendering, token cache pruning, binary search for find matches, region-based undo), clean separation of concerns in the Editor layer, and consistent theming architecture.

Following review, all minor and moderate findings were resolved:
- **EditorControl.cs** reduced from 2,052 to 1,977 lines (word-wrap coordinate mapping extracted to `WrapLayout`)
- **MainWindow.xaml.cs** reduced from 1,743 to 1,347 lines (session management, tab headers, and input dialog extracted)
- 50+ magic resource key strings replaced with compile-time constants
- Constructor injection replaced post-construction property assignment
- Duplicated code consolidated into shared utilities

**Finding counts:**

| Severity | Total | Resolved | Remaining |
|----------|-------|----------|-----------|
| Critical | 3 | 0 | 3 |
| Moderate | 12 | 11 | 0 |
| Minor | 10 | 8 | 0 |
| N/A | — | 2 skipped | — |
| **Total** | **25** | **21** | **3** |

*G7 (moderate) was noted as acceptable during review. G5/G6 (minor) were non-issues. D5 (minor, CI/CD) is infrastructure rather than code quality.*

---

## Readability

### R1. EditorControl.cs monolith (Moderate) — RESOLVED
**File:** `Volt/Editor/EditorControl.cs` (2,052 → 1,977 lines)

Word-wrap coordinate mapping (RecalcWrapData, LogicalToVisualLine, VisualToLogical, GetVisualY, GetPixelForPosition, VisualLineCount, WrapColStart) extracted to `Volt/Editor/WrapLayout.cs` (132 lines). EditorControl retains thin one-line delegating wrappers. All rendering code updated to use `_wrap.CumulOffset()`, `_wrap.CharsPerVisualLine`, etc.

### R2. MainWindow.xaml.cs monolith (Moderate) — RESOLVED
**File:** `Volt/UI/MainWindow.xaml.cs` (1,743 → 1,347 lines, −22.7%)

Three extractions:
- `SessionManager.cs` — session save/restore data logic (119 lines)
- `TabHeaderFactory.cs` — tab header creation and drag-to-reorder (210 lines)
- `ThemedInputBox.xaml/.cs` — XAML-based input dialog replacing 163 lines of procedural C#

### R3. Naming is generally excellent (Positive observation)
No changes needed.

### R4. Comments are purposeful and accurate (Positive observation)
No changes needed.

---

## Maintainability

### M1. `PromptForInput` builds UI entirely in C# code (Moderate) — RESOLVED
Created `Volt/UI/ThemedInputBox.xaml` and `ThemedInputBox.xaml.cs` — a proper XAML-based dialog with WindowChrome, themed styling via DynamicResource bindings, and a static `Show()` method. MainWindow's `PromptForInput` now delegates to `ThemedInputBox.Show()`.

### M2. Duplicated embedded resource extraction logic (Moderate) — RESOLVED
Created `Volt/EmbeddedResourceHelper.cs` with a single `ExtractAll(string resourcePrefix, string targetDir)` method. Both `SyntaxManager.EnsureDefaultGrammars()` and `ThemeManager.EnsureDefaultThemes()` now call this shared utility.

### M3. Duplicated file reading pattern in FileHelper (Minor) — RESOLVED
Extracted `ReadBytesAt(string path, long offset, int count)` and `ReadBytesAt(FileStream, long, int)` private helpers. `ReadTailVerifyBytes` and `VerifyAppendOnly` now delegate to these.

### M4. Tab header creation is entirely procedural (Moderate) — RESOLVED
Created `Volt/UI/TabHeaderFactory.cs` (210 lines) encapsulating tab header element creation, click-to-activate, drag-to-reorder, ghost popup, and drop indicator logic. MainWindow's `CreateTabHeader` is now a one-line delegation. All drag state fields (`_dragTab`, `_dragStartPos`, `_isTabDragging`, `_dragTargetIndex`, `_dragGhost`) moved to the factory.

### M5. Magic strings for DynamicResource keys (Moderate) — RESOLVED
Created `Volt/Theme/ThemeResourceKeys.cs` with 28 `const string` members. Replaced 50+ string literal references across `ThemeManager.cs`, `MainWindow.xaml.cs`, `ExplorerTreeControl.cs`, `CommandPalette.xaml.cs`, `DwmHelper.cs`, and `FindBar.xaml.cs`.

### M6. Magic numbers in FindBar positioning (Minor) — RESOLVED
Enhanced comments with explicit sync warnings and full arithmetic breakdown. Developers modifying title bar, tab strip, or status bar heights are now clearly warned to update these values.

### M7. `ColorTheme.ParseBrush` is called repeatedly without caching (Moderate) — RESOLVED
Added `_brushCache` dictionary to `ColorTheme`. `GetScopeBrush()` now caches parsed brushes on first access per scope, returning cached instances on subsequent calls within the same theme lifetime.

### M8. `ExplorerTreeControl` uses `FormattedText` in render loop (Minor) — RESOLVED
Added explanatory comment above `OnRender` documenting the intentional deviation: explorer has modest row counts (~50 visible) where FormattedText is acceptable and simpler than GlyphRun.

---

## Good Practices

### G1. No test framework configured (Critical) — OPEN
No unit test project exists. The extracted helper classes (TextBuffer, UndoManager, SelectionManager, FindManager, BracketMatcher, SyntaxManager, WrapLayout) are well-suited for unit testing.

### G2. Global mutable state via `App.Current` (Moderate) — RESOLVED
- `EditorControl` now accepts `ThemeManager` and `SyntaxManager` via constructor parameters (read-only `{ get; }` properties)
- `TabInfo` passes these through to the `EditorControl` constructor
- `ExplorerTreeControl` uses typed `App.Current` accessor instead of unsafe cast `((App)Application.Current)`

### G3. Thread safety of SyntaxManager (Critical) — OPEN
`_activeGrammar` remains a shared mutable field. Making grammar state per-editor is the recommended fix.

### G4. Exception handling is generally appropriate (Positive observation)
No changes needed.

### G5. `AtomicWriteText` retry loop (Minor) — N/A
Reviewed and found to be correct. No issue.

### G6. Encoding detection (Minor) — N/A
Standard behaviour for modern editors. Acceptable.

### G7. `UndoManager` stores full line copies (Moderate) — ACCEPTABLE
Noted as pragmatic and acceptable during review. The `MaxEntries = 200` cap and `IndentEntry` optimisation show awareness.

---

## Coupling & Architecture

### A1. `EditorControl` depends on `ThemeManager` via post-construction properties (Moderate) — RESOLVED
Constructor changed from parameterless to `EditorControl(ThemeManager, SyntaxManager)`. Properties changed from `{ get; set; } = null!` to `{ get; }` (read-only, set in constructor). No invalid intermediate state is possible.

### A2. Shared grammar across all editors (Critical) — OPEN
`_activeGrammar` is still a single shared field. Recommended fix: store the active `SyntaxDefinition` per-EditorControl.

### A3. Session save/restore logic is interleaved with MainWindow (Moderate) — RESOLVED
Created `Volt/UI/SessionManager.cs` with:
- `SaveSession(IReadOnlyList<TabInfo>, TabInfo?)` — builds session data, writes dirty content
- `RestoreSession(SessionSettings)` — returns `RestoredSession` data (list of `RestoredTab` records)

MainWindow retains UI-side concerns (creating tabs, activating, deferred scroll restore) but delegates data logic to `SessionManager`.

---

## Developer Experience

### D1. README is accurate and well-organized (Positive observation)
No changes needed.

### D2. CLAUDE.md is unusually thorough (Positive observation)
No changes needed.

### D3. No `.editorconfig` (Minor) — RESOLVED
Created `.editorconfig` at project root codifying existing conventions: file-scoped namespaces, bracing style, expression body preferences, indentation (4 spaces for C#, 2 for XAML/JSON).

### D4. Benchmark project is well-structured (Positive observation)
No changes needed.

### D5. No CI/CD pipeline (Minor) — SKIPPED
Infrastructure concern, not code quality. Deferred.

---

## Additional Observations

### O1. `CommandPaletteContext` record has 15 parameters (Minor) — RESOLVED
Split into grouped sub-records: `ExplorerActions(ToggleExplorer, OpenFolder, CloseFolder, RefreshLayout)` and `ProjectActions(New, Open, Save, Close)`. Main record now has 9 parameters.

### O2. `SetLanguageByExtension` uses linear search (Minor) — RESOLVED
Added `_extensionMap` (`Dictionary<string, SyntaxDefinition>` with `OrdinalIgnoreCase` comparer) built during `LoadGrammars()`. `SetLanguageByExtension` now does a single `TryGetValue` lookup.

### O3. `TabInfo` doesn't implement `IDisposable` (Minor) — RESOLVED
`TabInfo` now implements `IDisposable` with `Dispose()` calling `StopWatching()` and `GC.SuppressFinalize(this)`.

### O4. `SaveFilters` in FileHelper is hardcoded for Perl (Minor) — RESOLVED
Added `"JSON Files (*.json)|*.json"` and `"Markdown Files (*.md)|*.md"` to the save filter array.

---

## Summary Table

| # | Severity | Category | Status | Description |
|---|----------|----------|--------|-------------|
| G1 | Critical | Good Practices | **OPEN** | No test framework or tests |
| G3 | Critical | Good Practices | **OPEN** | Thread safety of `_activeGrammar` |
| A2 | Critical | Architecture | **OPEN** | Shared grammar state across all editors |
| R1 | Moderate | Readability | RESOLVED | EditorControl monolith → WrapLayout extracted |
| R2 | Moderate | Readability | RESOLVED | MainWindow monolith → 3 classes extracted |
| M1 | Moderate | Maintainability | RESOLVED | PromptForInput → ThemedInputBox.xaml |
| M2 | Moderate | Maintainability | RESOLVED | Duplicated resource extraction → EmbeddedResourceHelper |
| M4 | Moderate | Maintainability | RESOLVED | Tab headers → TabHeaderFactory |
| M5 | Moderate | Maintainability | RESOLVED | Magic strings → ThemeResourceKeys constants |
| M7 | Moderate | Maintainability | RESOLVED | ParseBrush → cached in ColorTheme |
| G2 | Moderate | Good Practices | RESOLVED | Global state → constructor injection |
| G7 | Moderate | Good Practices | ACCEPTABLE | Full line copies per edit (pragmatic) |
| A1 | Moderate | Architecture | RESOLVED | Post-construction DI → constructor params |
| A3 | Moderate | Architecture | RESOLVED | Session logic → SessionManager |
| M3 | Minor | Maintainability | RESOLVED | Duplicated reads → ReadBytesAt helper |
| M6 | Minor | Maintainability | RESOLVED | FindBar margins → enhanced sync comments |
| M8 | Minor | Maintainability | RESOLVED | FormattedText deviation → documented |
| D3 | Minor | Dev Experience | RESOLVED | Added .editorconfig |
| D5 | Minor | Dev Experience | SKIPPED | CI/CD (infrastructure) |
| O1 | Minor | Readability | RESOLVED | 15-param record → grouped sub-records |
| O2 | Minor | Good Practices | RESOLVED | Linear search → dictionary lookup |
| O3 | Minor | Good Practices | RESOLVED | TabInfo → IDisposable |
| O4 | Minor | Maintainability | RESOLVED | Save filters → added JSON, Markdown |
| G5 | Minor | Good Practices | N/A | No actual issue |
| G6 | Minor | Good Practices | N/A | Acceptable behaviour |

### New files created during remediation

| File | Lines | Purpose |
|------|-------|---------|
| `Volt/Editor/WrapLayout.cs` | 132 | Word-wrap coordinate mapping (from EditorControl) |
| `Volt/UI/TabHeaderFactory.cs` | 210 | Tab header creation and drag-to-reorder (from MainWindow) |
| `Volt/UI/SessionManager.cs` | 119 | Session save/restore data logic (from MainWindow) |
| `Volt/UI/ThemedInputBox.xaml` | ~80 | XAML-based themed input dialog (from MainWindow) |
| `Volt/UI/ThemedInputBox.xaml.cs` | 38 | ThemedInputBox code-behind |
| `Volt/Theme/ThemeResourceKeys.cs` | 36 | Centralised DynamicResource key constants |
| `Volt/EmbeddedResourceHelper.cs` | 33 | Shared embedded resource extraction |
| `.editorconfig` | 30 | Code style conventions |
