# Code Quality Review -- Volt Text Editor

**Date:** 2026-04-04
**Scope:** Full-project review of all source files (~10,400 lines of C# across 3 projects)
**Tests:** 81 passing (all green)

---

## Executive Summary

Volt is a well-structured, focused codebase with clean separation of concerns, good naming conventions, and consistent formatting throughout. The project demonstrates strong engineering fundamentals: extracted helper classes, performance-conscious rendering, thorough test coverage for core logic, and well-organized themes/grammars. The CLAUDE.md documentation is exceptionally detailed.

**Key concerns by priority:**
- **3 critical issues** -- a thread-safety bug in the shared `SyntaxManager._claimedBuf` buffer, stale `CharCount` after certain undo/redo operations, and `ReplaceAll` leaving the caret at an invalid position
- **30 moderate issues** -- triplicated session restore logic in MainWindow, hardcoded theme resource key strings in the panel system, `AppSettings.cs` as a god file, dense drag logic in PanelShell, and bracket matching that ignores syntax context
- **42 minor issues** -- small memory leaks, missing validations, inconsistent patterns, and style nits

The codebase is in good shape for a project of this size and ambition. The critical issues are localized and fixable. Most moderate findings involve consolidating duplicated patterns or enforcing the project's own design rules.

---

## Critical Issues

### C1. Thread-safety: shared mutable `_claimedBuf` in SyntaxManager
**File:** `Editor/SyntaxManager.cs:23`

`_claimedBuf` is an instance-level `bool[]` reused across all `Tokenize()` calls. Since `SyntaxManager` is a singleton owned by `App`, and `EditorControl.PrecomputeLineStates` dispatches tokenization at idle priority, multiple tabs or a single tab's background precompute and render-path tokenization can race and corrupt each other's claimed state.

**Suggestion:** Either make `_claimedBuf` a `[ThreadStatic]` field, pass it as a parameter from a thread-local pool, or allocate it per-call (it's small -- `line.Length` bools).

### C2. Stale `CharCount` after `ReplaceLines` in certain code paths
**File:** `Editor/TextBuffer.cs:160-169`

In `ReplaceLines`, when `_maxLineLengthDirty` is false and `removingMax` is false, the `else` branch updates `_maxLineLength` for new lines but never sets `_charCountDirty = true`. After `ReplaceLines` is called (e.g., by undo/redo), the `CharCount` property returns stale data, causing the status bar to display incorrect character counts.

**Suggestion:** Set `_charCountDirty = true` unconditionally at the top of `ReplaceLines`, since the buffer content always changes.

### C3. `ReplaceAll` does not update caret position after replacements
**File:** `Editor/EditorControl.cs:1994-2012`

After replacing all matches (which may change line lengths and line counts if the replacement differs in length), `_caretLine`/`_caretCol` are never adjusted. The caret could end up pointing past the end of a line or at a nonexistent line. `EndEdit` records the current (invalid) caret position.

**Suggestion:** After `ReplaceAll`, clamp the caret to valid buffer bounds or set it to the position of the first replacement.

---

## Readability

### R1. `MainWindow.Editor` property is a null-dereference hazard -- MODERATE
**File:** `UI/MainWindow.xaml.cs:24`

`private EditorControl Editor => _activeTab!.Editor;` uses the null-forgiving operator. Most call sites guard with `if (_activeTab == null) return;` first, but the accessor itself will crash if any future caller forgets the guard.

**Suggestion:** Make the property return-type nullable (`EditorControl?`) or add a null-check inside with a clear exception.

### R2. `OnKeyDown` if-else chain in MainWindow -- MODERATE
**File:** `UI/MainWindow.xaml.cs:1246-1268`

A 22-line chain of `else if` statements each re-checking `ctrl` and `shift` modifier flags. Hard to scan and easy to introduce ordering bugs.

**Suggestion:** Use a dictionary-based dispatch keyed on `(Key, Modifiers)` tuples, or a switch expression.

### R3. Unused static typefaces in ExplorerTreeControl -- MODERATE
**File:** `UI/ExplorerTreeControl.cs:30-31`

`SemiBoldTypeface` and `ItalicTypeface` are declared as `static readonly` fields but never referenced anywhere. Dead code.

**Suggestion:** Remove them.

### R4. PanelShell `OnMouseMove` drag logic is dense and deeply nested -- MODERATE
**File:** `UI/Panels/PanelShell.xaml.cs:498-575`

~77 lines with nested conditionals, proportional calculations, and a magic `const double threshold = 0.25`. Lines 524-527 pack four nearly identical conditions into single lines, making the hit-testing logic hard to follow.

**Suggestion:** Extract zone detection into a private `DetectDropZone(Point pos, Size size, PanelPlacement? currentPlacement, bool sourceHasOtherTabs)` method.

### R5. `OnNew` is a pointless wrapper -- MINOR
**File:** `UI/MainWindow.xaml.cs:1101`

`private void OnNew(object sender, RoutedEventArgs e) => OnNewTab(sender, e);` adds no value.

### R6. `_settings` field should be `readonly` -- MINOR
**File:** `UI/MainWindow.xaml.cs:17`

Assigned only once in the constructor, never reassigned.

### R7. `ApplyRegionSize` sets `MaxWidth = Infinity` when collapsing -- MINOR
**File:** `UI/Panels/PanelShell.xaml.cs:299,304`

Setting `MaxWidth/MaxHeight` to `double.PositiveInfinity` when collapsing (size == 0) is dead/misleading code. The `Width = new GridLength(0)` takes precedence.

### R8. `RegexOptions.Multiline` on single-line grammar rules -- MINOR
**File:** `Editor/SyntaxDefinition.cs:99`

All rules are compiled with `Multiline`, which changes `^`/`$` behavior. Since tokenization operates on single lines, this flag has no effect but is misleading.

### R9. `SaveTabAs` hardcodes filter index mapping -- MINOR
**File:** `UI/MainWindow.xaml.cs:957-966`

The `filterIndex` switch expression maps only `.pl` and `.txt` and defaults everything else to index 3 (JSON). Gives the wrong default filter for most file types.

---

## Maintainability

### M1. Triplicated session restore logic in MainWindow -- MODERATE
**Files:** `UI/MainWindow.xaml.cs:610-686`, `709-776`, and `1547-1601`

`RestoreSession`, `RestoreFolderTabs`, and `RestoreWorkspaceSession` contain ~60 lines each of nearly identical tab-creation logic (file loading, encoding detection, dirty state, deferred caret/scroll restore via `Loaded` event). `RestoreWorkspaceSession` uses `WorkspaceSessionTab` instead of `RestoredTab` but the pattern is the same.

**Suggestion:** Extract a shared `RestoreTabFromSession(...)` method that handles the common pattern.

### M2. `ForceCloseTab` duplicates most of `CloseTab` -- MODERATE
**File:** `UI/MainWindow.xaml.cs:191-227 and 499-526`

Both methods remove tab from list, remove header from TabStrip, unhook events, stop watching, release resources, and activate next tab. The only difference is `ForceCloseTab` skips the dirty-save prompt.

**Suggestion:** Consolidate into `CloseTab(bool force = false)`.

### M3. Hardcoded theme resource key strings in Panel system -- MODERATE
**Files:** `UI/Panels/PanelShell.xaml.cs:538`, `UI/Panels/TabRegion.cs:48,56,118,123,142,158,174,191,192`

Ten instances of hardcoded string literals like `"ThemeTextFg"`, `"ThemeExplorerHeaderBg"`, `"ThemeTabActive"` instead of using `ThemeResourceKeys` constants. This directly violates the project's own design rule documented in CLAUDE.md and risks silent typo bugs.

**Suggestion:** Replace all with `ThemeResourceKeys.*` constants.

### M4. `AppSettings.cs` is a god file with 8 classes -- MODERATE
**File:** `AppSettings.cs:1-255`

Contains `ApplicationSettings`, `FontSettings`, `CaretSettings`, `FindSettings`, `ExplorerSettings`, `EditorSettings`, `SessionTab`, `SessionSettings`, and `AppSettings` in one file. `SessionSettings` alone is ~100 lines with its own file I/O logic, mixing data model with persistence operations.

**Suggestion:** At minimum, extract `SessionSettings` (with its I/O methods) into its own file. Consider splitting the DTOs into a separate `SettingsModels.cs`.

### M5. `ReplaceCurrent` doesn't re-run find search -- MODERATE
**File:** `Editor/EditorControl.cs:1981`

After replacing text at the current match position, match positions stored in `FindManager._matches` become stale. Subsequent matches on the same line will have incorrect column offsets.

**Suggestion:** Trigger `_find.Search()` after replacement.

### M6. Bracket matching ignores syntax context -- MODERATE
**File:** `Editor/BracketMatcher.cs`

Scans raw text without consulting syntax tokens. Brackets inside strings, comments, or regex patterns are matched as if they were code (e.g., `"("` is matched as an opening paren).

**Suggestion:** Accept token information and skip brackets within string/comment scopes.

### M7. `FormattedText` allocated per visible row per render in ExplorerTreeControl -- MODERATE
**File:** `UI/ExplorerTreeControl.cs:370-376`

A new `FormattedText` object per visible row on every `OnRender`. Could be cached by name+width to reduce GC pressure during rapid scrolling.

### M8. Up/Down arrow navigation ignores word wrap -- MODERATE
**File:** `Editor/EditorControl.cs:1586-1605`

Up/Down handlers move by logical lines. When word wrap is enabled, pressing Up/Down should move by visual (wrapped) lines. Currently skips the entire logical line in one keypress.

### M9. `_lineNumStrings` dictionary grows unboundedly -- MODERATE
**File:** `Editor/EditorControl.cs:114`

Caches line number strings but is only cleared on `SetContent()`. For a 1M-line file scrolled end-to-end, this accumulates up to 1M dictionary entries. Not cleared in `ReleaseResources()` either.

**Suggestion:** Prune to viewport window (like token cache), or clear in `ReleaseResources()`.

### M10. Duplicated collapse logic in PanelShell -- MODERATE
**File:** `UI/Panels/PanelShell.xaml.cs:395-409 vs 106-119`

`OnRegionCloseRequested` and the collapse branch of `ToggleRegion` both iterate panels in a region, add to `_collapsedByToggle`, set `IsVisible = false`, call `RemovePanel`, fire `PanelLayoutChanged`, and call `CollapseRegion`.

**Suggestion:** Extract a shared `CollapseRegionPanels(PanelPlacement)` method.

### M11. Duplicated `ControlTemplate` blocks in MainWindow.xaml -- MODERATE
**File:** `UI/MainWindow.xaml:62-144`

`MenuItemDropdownStyle` and `MenuItemCheckableStyle` share nearly identical templates. The checkable style only adds a `CheckMark` TextBlock and one extra trigger.

**Suggestion:** Use a single base template with an optional checkmark element that's collapsed by default.

### M12. `PromptSaveDirtyTabs` ignores cancel return value -- MODERATE
**File:** `UI/MainWindow.xaml.cs:1502-1512`

If the user clicks "Cancel" on a save prompt, the result is silently ignored and the next dirty tab is prompted. The user cannot cancel a workspace-close operation mid-way through dirty tab prompts.

**Suggestion:** Check the return value and abort if the user cancels.

### M13. TabRegion builds ControlTemplate programmatically instead of reusing existing style -- MODERATE
**File:** `UI/Panels/TabRegion.cs:162-176`

The close button's hover template is built in code via `FrameworkElementFactory` (~15 lines of imperative template construction). The `TabCloseButton` style already exists in `App.xaml`.

**Suggestion:** Apply the existing style: `closeBtn.Style = (Style)Application.Current.FindResource("TabCloseButton");`

### M14. CommandPalette manual selection management instead of ListBox built-in -- MODERATE
**File:** `UI/CommandPalette.xaml.cs:332-347`

Maintains a manual `_selectedIndex` and iterates all items to set backgrounds, bypassing ListBox's native `SelectedIndex`/`SelectedItem`. Can cause internal selection state to drift from visual state.

**Suggestion:** Use `_commandList.SelectedIndex` directly and style selection via ListBoxItem triggers.

### M15. `IsCaretInsideString` has a fallback heuristic that duplicates SyntaxManager logic -- MODERATE
**File:** `Editor/EditorControl.cs:468-504`

When there are no syntax tokens (lines 490-504), a hand-rolled quote-tracking loop is used. This is similar to `DetectUnclosedString` in `SyntaxManager` but subtly different (checks both `'` and `"` without the word-character guard). Bugs fixed in one may not be fixed in the other.

**Suggestion:** Extract into a shared static method, or always rely on the tokenizer path.

### M16. App.xaml is a 508-line monolith -- MODERATE
**File:** `App.xaml:1-508`

Mixes default brush resources, 7+ button/control styles, scrollbar templates, context menu templates, and a ScrollViewer template in a single file.

**Suggestion:** Split into separate ResourceDictionary files (e.g., `Styles/Buttons.xaml`, `Styles/ScrollBars.xaml`, `Styles/Menus.xaml`) and merge them in App.xaml.

### M17. `ThemeOverlayBg` not centralized in ThemeResourceKeys -- MINOR
**File:** `Theme/ThemeResourceKeys.cs`

`ThemeOverlayBg` is defined in `App.xaml` and used in `CommandPalette.xaml` but has no corresponding constant in `ThemeResourceKeys.cs`, violating the centralization design rule.

### M18. `WrapLayout.Recalculate` never shrinks arrays -- MINOR
**File:** `Editor/WrapLayout.cs:39-44`

After opening a large file then a small one, wrap arrays remain oversized.

### M19. `PanelRegistration.DefaultSize` is stored but never read -- MINOR
**File:** `UI/Panels/PanelShell.xaml.cs:633`

Dead field.

### M20. Double theme application on startup -- MINOR
**File:** `Theme/ThemeManager.cs:43` and `App.xaml.cs:19`

`Initialize()` applies "Dark" theme, then `OnStartup` immediately applies the user's chosen theme. Wastes work on every launch.

### M21. `SessionSettings.SessionDir` is a redundant static copy -- MINOR
**File:** `AppSettings.cs:60`

Duplicates `AppPaths.SessionDir`. Should reference the original directly.

### M22. Workspace vs folder session tab types use inconsistent property names -- MINOR
**File:** `UI/Workspace.cs` vs `AppSettings.cs`

`WorkspaceSessionTab` uses `ScrollX`/`ScrollY` while `SessionTab` uses `ScrollVertical`/`ScrollHorizontal`. Two parallel types for the same concept with different naming.

### M23. Repeated button template pattern in App.xaml -- MINOR
**File:** `App.xaml:52-66, 68-83, 234-295, 298-348`

Six button styles define nearly identical ControlTemplates (Border + ContentPresenter + IsMouseOver trigger). Only the hover background brush differs.

**Suggestion:** Create a base template and derive styles from it.

---

## Good Practices

### G1. `FolderBrowserDialog` not disposed -- MODERATE
**File:** `UI/MainWindow.xaml.cs:400,1358,1607`

`System.Windows.Forms.FolderBrowserDialog` implements `IDisposable` but is used without `using` statements.

### G2. `TabInfo` implements `IDisposable` but is never disposed -- MODERATE
**File:** `UI/TabInfo.cs:9`

`CloseTab`, `ForceCloseTab`, and `CloseAllTabs` call `StopWatching()` directly instead of `Dispose()`. Either call `Dispose()` or remove the `IDisposable` interface.

### G3. `async void` methods in FileTreeItem swallow exceptions -- MODERATE
**File:** `UI/FileTreeItem.cs:83,188`

`LoadChildren` and `RefreshChildren` are `async void`. Unhandled exceptions beyond the caught `UnauthorizedAccessException`/`IOException` will crash the application.

**Suggestion:** Add a top-level catch-all with logging to prevent fatal crashes.

### G4. `ContextMenuHelper.Item` captures brush at creation time -- MODERATE
**File:** `UI/ContextMenuHelper.cs:35`

Uses `(Brush)Application.Current.Resources[ThemeResourceKeys.TextFg]` which captures the current brush. Theme changes while the context menu is open won't update icon colors.

**Suggestion:** Use `SetResourceReference` for dynamic binding.

### G5. No filename validation in `DoNewFile`/`DoNewFolder` -- MODERATE
**File:** `UI/FileExplorerPanel.xaml.cs:275-313`

Invalid characters in user input (e.g., `<`, `>`, `:`, `|`) produce raw system error messages via the generic catch block instead of user-friendly validation.

**Suggestion:** Pre-validate with `Path.GetInvalidFileNameChars()`.

### G6. TabRegion event handler closures not unsubscribed on panel removal -- MODERATE
**File:** `UI/Panels/TabRegion.cs:199-238`

`MouseLeftButtonDown`, `MouseMove`, `MouseLeftButtonUp`, and `MouseRightButtonUp` lambda closures are attached to the header Border but never detached in `RemovePanel`. The header is removed from the visual tree, so this is unlikely to cause real leaks, but it is not clean.

**Suggestion:** Unsubscribe mouse handlers in `RemovePanel`.

### G7. FindBar magic constants must stay in sync with XAML layout -- MODERATE
**File:** `UI/FindBar.xaml.cs:16-17`

`FindBarTopMargin = 34` and `FindBarBottomMargin = 19` are documented as needing to match MainWindow.xaml layout heights, but there's no compile-time or runtime enforcement.

**Suggestion:** Compute dynamically from actual layout element sizes.

### G8. SettingsWindow `SelectNav` uses string matching for section names -- MODERATE
**File:** `UI/SettingsWindow.xaml.cs:81-93`

`SelectNav("Theme")`, `SelectNav("Font")` etc. use string comparisons to toggle visibility. An enum would prevent typos.

### G9. `FontManager.Apply()` fires events even for no-op changes -- MINOR
**File:** `Editor/FontManager.cs:87`

Every property setter calls `Apply()` which triggers events even when setting the same value. Only `LineHeightMultiplier` has an early-exit guard.

### G10. `FindEnclosing` allocates a `Dictionary` on every caret movement -- MINOR
**File:** `Editor/BracketMatcher.cs:63`

Called on every caret move via `FindMatch`. A reusable structure or stack-allocated array would reduce GC pressure.

### G11. `SyntaxDefinition.LoadFromFile` catches all exceptions silently -- MINOR
**File:** `Editor/SyntaxDefinition.cs:170`

Catches `Exception` and returns null. Should be narrowed to `IOException`, `JsonException`, and `RegexParseException`.

### G12. `AtomicWriteText` retry loop blocks the UI thread -- MINOR
**File:** `UI/FileHelper.cs:60-76`

`Thread.Sleep(50)` in a retry loop. If called from the UI thread, this blocks the UI for up to 150ms.

### G13. `FolderSessions` dictionary is case-sensitive on Windows -- MINOR
**File:** `AppSettings.cs:165`

Uses default `StringComparer`. On Windows, `C:\Foo` and `c:\foo` are the same path but would be treated as different keys.

**Suggestion:** Use `StringComparer.OrdinalIgnoreCase`.

### G14. `EmbeddedResourceHelper.ExtractAll` uses non-atomic writes -- MINOR
**File:** `EmbeddedResourceHelper.cs:31`

Uses `File.WriteAllText` while the rest of the codebase uses `FileHelper.AtomicWriteText`.

### G15. `ColorTheme.ParseBrush` doesn't handle null input -- MINOR
**File:** `Theme/ColorTheme.cs:83`

`ColorConverter.ConvertFromString` can throw `NullReferenceException` if `hex` is null, bypassing the `FormatException` catch.

### G16. DWM API return values silently discarded -- MINOR
**File:** `UI/DwmHelper.cs:31,38,46`

`DwmSetWindowAttribute` returns an HRESULT that is never checked.

### G17. `Xunit.StaFact` uses floating version `1.*` -- MINOR
**File:** `Volt.Tests/Volt.Tests.csproj:14`

Floating versions can cause non-reproducible builds.

### G18. `WorkspaceSession` doesn't persist dirty content -- MINOR
**File:** `UI/Workspace.cs:17`

Workspace sessions track `IsDirty` but don't save unsaved content. When reopening, dirty tabs lose changes -- inconsistent with per-folder sessions.

### G19. `RestoreWorkspaceSession` tab index drift -- MINOR
**File:** `UI/MainWindow.xaml.cs:1591-1599`

Deferred scroll/caret restore assumes `_tabs[i]` corresponds to `workspace.Session.Tabs[i]`, but skipped missing files cause indices to drift.

### G20. `_dragGhost` Popup may leak if mouse capture is lost -- MINOR
**File:** `UI/TabHeaderFactory.cs:139-147`

No `LostMouseCapture` handler to clean up the ghost popup if capture is stolen by a system dialog.

### G21. `ExpandInterpolation` allocates even when no expansion needed -- MINOR
**File:** `Editor/SyntaxManager.cs:479`

A new `List<SyntaxToken>` is allocated and all tokens copied even when no string tokens are found.

### G22. `HandleTab` with shift and no multi-line selection creates an empty undo entry -- MINOR
**File:** `Editor/EditorControl.cs:1541-1549`

When `shift` is true and there is no multi-line selection, the code enters `BeginEdit`/`FinishEdit` but does nothing between them.

### G23. `WorkspaceManager.OpenWorkspace` doesn't handle `JsonException` -- MINOR
**File:** `UI/WorkspaceManager.cs:23-33`

If the `.volt-workspace` file contains invalid JSON, `JsonSerializer.Deserialize` throws an unhandled exception with no user-friendly message.

### G24. `FlushStagedDeletes` swallows all exceptions silently -- MINOR
**File:** `UI/FileExplorerPanel.xaml.cs:499`

`catch { /* best effort */ }` -- at minimum a `Debug.WriteLine` would help diagnose issues.

### G25. Duplicated `MakeBuffer` helper across 4 test files -- MINOR
**Files:** `BracketMatcherTests.cs:8-13`, `FindManagerTests.cs:8-13`, `SelectionManagerTests.cs:8-13`, `WrapLayoutTests.cs:8-13`

Four test classes define the identical `MakeBuffer` helper method.

**Suggestion:** Extract to a shared `TestHelpers` class.

### G26. SyntaxManager tests have filesystem side effects -- MINOR
**File:** `Volt.Tests/SyntaxManagerTests.cs:9-13`

`Initialize()` extracts embedded grammars to `%AppData%/Volt/Grammars/`. Tests that call this modify the user's file system.

---

## Developer Experience

### D1. README is accurate but minimal -- MINOR

The README covers features, build instructions, and shortcuts but is missing:
- Contribution guidelines
- Environment variable documentation (e.g., `%AppData%/Volt/` paths)
- How to add a new language grammar or theme (step-by-step)
- Screenshot or visual preview

### D2. CLAUDE.md test count is stale -- MINOR
**File:** `CLAUDE.md`

States "83 tests" but the suite currently has 81 passing tests.

### D3. Project structure section in README is outdated -- MINOR
**File:** `README.md:54-60`

The `UI/` description mentions only "MainWindow, FindBar, CommandPalette, SettingsWindow" but omits the file explorer, panels system, workspace management, session management, and several other files.

### D4. `UseWindowsForms` dependency is unexplained -- MINOR
**File:** `Volt/Volt.csproj:9`

WinForms is enabled (presumably for `FolderBrowserDialog`) with `Using Remove` directives to suppress its namespaces. A comment explaining why would help newcomers.

### D5. No test coverage for panel drag-to-dock or splitter persistence -- MINOR
**File:** `Volt.Tests/PanelShellTests.cs`

Tests cover basic show/hide/move but the most complex behaviors (drag-to-dock, splitter resize persistence) are untested.

### D6. Empty Explorer section in SettingsWindow -- MINOR
**File:** `UI/SettingsWindow.xaml:393-402`

The Explorer scroller contains only a header with no settings controls. Should either be populated or hidden.

---

## Summary Table

| Category | Critical | Moderate | Minor | Total |
|----------|----------|----------|-------|-------|
| **Core Bugs** | 3 | 0 | 0 | 3 |
| **Readability** | 0 | 4 | 5 | 9 |
| **Maintainability** | 0 | 16 | 7 | 23 |
| **Good Practices** | 0 | 8 | 18 | 26 |
| **Developer Experience** | 0 | 0 | 6 | 6 |
| **Total** | **3** | **28** | **36** | **67** |

### Resolution Status

**Fixed (48 items):** C1, C2, C3, R1, R3, R5, R6, R7, R8, M2, M3, M5, M9, M10, M11, M12, M13, M17, M18, M19, M20, M21, M23, G1, G2, G3, G4, G5, G6, G7, G8, G9, G10, G11, G13, G14, G15, G16, G17, G20, G21, G22, G23, G24, G25, D2, D3, D4

**Deferred (19 items) -- require larger refactors or feature work:**
- M1: Triplicated session restore logic -- high risk, touches critical startup paths
- M4: Split AppSettings.cs -- file reorganization, no behavioral improvement
- M6: Syntax-aware bracket matching -- feature change, needs design work
- M7: ExplorerTreeControl FormattedText caching -- needs careful invalidation logic
- M8: Up/Down navigation with word wrap -- behavioral change requiring extensive testing
- M14: CommandPalette ListBox selection -- UI behavior change with subtle interaction risks
- M15: IsCaretInsideString dedup -- requires careful analysis of subtle differences
- M16: Split App.xaml into ResourceDictionaries -- structural change, no behavioral improvement
- M22: Inconsistent session tab property names -- API rename with ripple effects
- R2: OnKeyDown if-else chain -- cosmetic refactor with high risk of introducing bugs
- R4: PanelShell OnMouseMove density -- cosmetic, works correctly as-is
- R9: SaveTabAs filter index -- low impact
- G12: AtomicWriteText blocks UI thread -- needs async conversion
- G18: Workspace session dirty content persistence -- feature work
- G19: RestoreWorkspaceSession tab index drift -- needs careful analysis
- G26: SyntaxManager tests filesystem side effects -- test infrastructure change
- D1: README expansion -- subjective content
- D5: Panel drag-to-dock test coverage -- feature work
- D6: Empty Explorer settings section -- placeholder for future settings
