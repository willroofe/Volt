# General Code Review — TextEdit

**Date**: 2026-04-01
**Scope**: Full project review — all source files in `TextEdit/`
**Reviewer**: Claude (automated)

---

## CRITICAL

No critical findings. The application is a local desktop WPF text editor with no network services, no database, no web output, and no credential storage. The attack surface is limited to local file I/O and clipboard operations.

---

## WARNING

### EditorControl.cs — HandlePaste: selection deleted but undo entry lost on clipboard failure

**Lines 1421–1428**

```csharp
var scope = BeginEdit(sl, el);
DeleteSelectionIfPresent();        // ← buffer modified here

string text;
try { text = Clipboard.GetText(); }
catch (System.Runtime.InteropServices.ExternalException) { return; }  // ← early return, no EndEdit
```

If `Clipboard.GetText()` throws (clipboard locked by another process), the function returns without calling `EndEdit`/`FinishEdit`. The user's selected text has already been deleted from the buffer, but no undo entry was pushed. The deletion is permanent and cannot be undone.

**Risk**: Data loss — user loses selected text with no way to recover it.

**Suggested fix**: Move the clipboard read before any buffer mutation:

```csharp
string text;
try { text = Clipboard.GetText(); }
catch (System.Runtime.InteropServices.ExternalException) { return; }

var (sl, el) = GetEditRange();
var scope = BeginEdit(sl, el);
DeleteSelectionIfPresent();
// ... continue with paste
```

---

### EditorControl.cs — HandlePaste: direct buffer indexer bypasses mutation methods

**Lines 1442, 1449**

```csharp
_buffer[_caretLine] += pasteLines[0];    // line 1442
// ...
_buffer[_caretLine] += after;            // line 1449
```

These lines concatenate strings directly via the `TextBuffer` indexer setter, bypassing `InsertAt`/`ReplaceAt` which handle `NotifyLineChanging` for max-line-length cache invalidation. If the line being modified was previously the longest line and is being replaced with a shorter result (not the case here — both are appending), the cache could become stale.

In practice, since both operations append and `UpdateExtent` is called in `FinishEdit`, this is currently safe. However, it violates the project's own design rule documented in CLAUDE.md: "Always use `TextBuffer` methods (`InsertAt`, `DeleteAt`, `ReplaceAt`, `JoinWithNext`, `TruncateAt`)."

**Risk**: Future modifications to the paste handler or TextBuffer caching logic could introduce stale max-length bugs.

**Suggested fix**: Use `_buffer.InsertAt(_caretLine, _buffer[_caretLine].Length, pasteLines[0])` and similar for the `after` append.

---

### SelectionManager.cs — DeleteSelection: direct buffer mutation bypasses TextBuffer methods

**Lines 77–78 (single-line), 81–82 (multi-line)**

```csharp
// Single-line case:
buffer.NotifyLineChanging(sl);
buffer[sl] = buffer[sl][..sc] + buffer[sl][ec..];  // direct indexer

// Multi-line case:
buffer[sl] = buffer[sl][..sc] + buffer[el][ec..];  // no NotifyLineChanging
buffer.RemoveRange(sl + 1, el - sl);
```

The multi-line case modifies `buffer[sl]` without calling `NotifyLineChanging`. If line `sl` was the longest line and the concatenation produces a shorter result, `_maxLineLength` would be stale until `RemoveRange` sets `_maxLineLengthDirty = true`. The single-line case correctly calls `NotifyLineChanging` but still uses the direct indexer instead of `ReplaceAt`.

**Risk**: If `RemoveRange` is ever refactored to not set the dirty flag, the multi-line case would silently produce stale max-length values, causing horizontal scrollbar extent miscalculation.

**Suggested fix**: Use `buffer.DeleteAt` or `buffer.ReplaceAt` for the single-line case, and add `buffer.NotifyLineChanging(sl)` before the multi-line concatenation.

---

### MainWindow.xaml.cs — RestoreWindowPosition: null dereference on WindowLeft/WindowTop

**Lines 533–536**

```csharp
if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
{
    double left = _settings.WindowLeft!.Value;   // ← not null-checked
    double top = _settings.WindowTop!.Value;     // ← not null-checked
```

The guard only checks `WindowWidth` and `WindowHeight`. If a settings file has width/height but no left/top (e.g., manually edited, partially migrated), `WindowLeft` and `WindowTop` would be null and `.Value` would throw `InvalidOperationException`.

**Risk**: App crash on startup with certain malformed settings files.

**Suggested fix**: Add `_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue` to the guard condition.

---

### FontManager.cs — Apply: potential NullReferenceException if no GlyphTypeface found

**Lines 78–85**

```csharp
GlyphTypeface? gt = null;
if (_monoTypeface.TryGetGlyphTypeface(out gt)) { }
else
{
    foreach (var face in new FontFamily(familyName).GetTypefaces())
        if (face.TryGetGlyphTypeface(out gt)) break;
}
if (gt != null) _glyphTypeface = gt;  // ← if null, _glyphTypeface retains previous value
```

If both attempts fail to find a `GlyphTypeface` (e.g., unusual font with no TrueType outlines), `_glyphTypeface` retains its previous value. On the very first `Apply()` call from the constructor, `_glyphTypeface` is declared as `null!`, meaning `DrawGlyphRun` would throw a `NullReferenceException` when accessing `_glyphTypeface.CharacterToGlyphMap`.

**Risk**: App crash if the default font (Cascadia Code / Consolas) is somehow unavailable.

**Suggested fix**: Fall back to a known-safe typeface or throw a descriptive exception in the constructor if no glyph typeface is found.

---

### FileHelper.cs — DetectEncoding: UTF-32 LE misidentified as UTF-16 LE

**Lines 66–75**

```csharp
if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)     // UTF-16 LE
    return new UnicodeEncoding(false, true);
if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)     // UTF-16 BE
    return new UnicodeEncoding(true, true);
if (read >= 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF)  // UTF-32 BE
    return new UTF32Encoding(true, true);
```

The UTF-16 LE check (`FF FE`) matches before the UTF-32 LE BOM (`FF FE 00 00`) is tested. UTF-32 LE files would be decoded as UTF-16 LE, producing garbled output with every other character being `\0`.

Additionally, UTF-32 LE (`FF FE 00 00`) is never explicitly checked.

**Risk**: UTF-32 LE encoded files opened incorrectly; rare but silent data corruption on display.

**Suggested fix**: Check 4-byte BOMs before 2-byte BOMs:

```csharp
if (read >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0 && bom[3] == 0)
    return new UTF32Encoding(false, true);   // UTF-32 LE
if (read >= 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF)
    return new UTF32Encoding(true, true);    // UTF-32 BE
// Then UTF-16 checks...
```

---

### FileHelper.cs — AtomicWriteText: orphaned temp file on move failure

**Lines 43–47**

```csharp
var tempPath = Path.Combine(dir, Path.GetRandomFileName());
File.WriteAllText(tempPath, content, encoding);
File.Move(tempPath, path, overwrite: true);
```

If `File.Move` throws (e.g., target file is locked by another process, permissions issue), the temp file is left in the same directory as the target. Repeated save failures accumulate orphaned temp files.

**Risk**: Disk clutter; user confusion seeing random-named files alongside their documents.

**Suggested fix**: Wrap in try/finally to clean up the temp file on failure:

```csharp
try
{
    File.WriteAllText(tempPath, content, encoding);
    File.Move(tempPath, path, overwrite: true);
}
catch
{
    try { File.Delete(tempPath); } catch { }
    throw;
}
```

---

### ThemeManager.cs / SyntaxManager.cs — EnsureDefault*: unhandled I/O on startup

**ThemeManager.cs lines 148–153, SyntaxManager.cs lines 568–585**

```csharp
Directory.CreateDirectory(ThemesDir);
WriteEmbeddedResource("TextEdit.Resources.Themes.default-dark.json", ...);
```

Neither `EnsureDefaultThemes` nor `EnsureDefaultGrammars` has error handling. If `%AppData%` is on a read-only filesystem, disk is full, or the path is inaccessible, an unhandled exception crashes the app before the main window appears.

**Risk**: App fails to start with an unhelpful stack trace if the AppData directory is unavailable.

**Suggested fix**: Wrap each embedded resource extraction in a try/catch, or wrap the entire `Initialize()` method. Log or ignore failures — the app can function with an empty themes/grammars directory.

---

### MainWindow.xaml.cs — OnKeyDown: null-forgiving operator on _activeTab

**Line 814**

```csharp
else if (ctrl && !shift && e.Key == Key.W) { CloseTab(_activeTab!); e.Handled = true; }
```

`_activeTab` is declared as `TabInfo?` and could theoretically be null. While the constructor always calls `ActivateTab`, the `!` operator suppresses the compiler warning rather than handling the null case.

**Risk**: Low — would only trigger if internal state management breaks. But the `!` operator makes this invisible to static analysis.

**Suggested fix**: Add a null guard: `if (_activeTab != null) CloseTab(_activeTab);`

---

## INFO

### AppSettings.cs — Settings loaded twice on startup

**App.xaml.cs line 17, MainWindow.xaml.cs line 45**

```csharp
// App.OnStartup:
var settings = AppSettings.Load();          // load #1 — used only for theme name
ThemeManager.Apply(settings.Application.ColorTheme);

// MainWindow constructor:
_settings = AppSettings.Load();             // load #2 — the "real" settings
```

The settings file is deserialized twice. The first load in `App.OnStartup` is used solely to get the theme name, then discarded.

**Suggested fix**: Pass the loaded settings instance to `MainWindow` via constructor parameter or a static property on `App`.

---

### AppSettings.cs — MigrateOldFormat silently writes on load

**Line 123**

```csharp
// Save in new format so migration only happens once
s.Save();
```

`AppSettings.Load()` has a side effect: if the old format is detected, it silently overwrites the settings file. This is generally fine, but callers of `Load()` don't expect it to write to disk.

**Suggested fix**: Document this behavior, or return a flag indicating migration occurred so the caller can decide when to save.

---

### Multiple files — Broad catch clauses swallow errors silently

The following locations use bare `catch` or `catch { }` that silently swallow all exceptions:

| File | Lines | Context |
|------|-------|---------|
| `AppSettings.cs` | 84 | `Load()` — corrupt settings file |
| `SyntaxDefinition.cs` | 96–104 | `Compile()` — invalid regex patterns |
| `SyntaxDefinition.cs` | 160–168 | `LoadFromFile()` — corrupt grammar JSON |
| `ColorTheme.cs` | 93–103 | `LoadFromFile()` — corrupt theme JSON |
| `ThemeManager.cs` | 88 | `LoadThemeCache()` — theme parse failure |
| `FontManager.cs` | 146 | `GetMonospaceFonts()` — font enumeration |

In all cases, failure is silent. A corrupted grammar file produces no syntax highlighting with no indication why. A corrupted theme file simply disappears from the list.

**Suggested fix**: For a desktop app without a logging framework, these are acceptable fallbacks. However, consider adding `Debug.WriteLine` traces so issues are visible during development. At minimum, `SyntaxDefinition.Compile()` should report which pattern failed, as invalid regex in grammar JSON is a common authoring error.

---

### EditorControl.cs — HandleReturn: indent calculation relies on string indexing from end

**Line 1109**

```csharp
var indent = _buffer[_caretLine][..^(_buffer[_caretLine].TrimStart().Length)];
```

This extracts leading whitespace by taking `line[..^trimmedLength]`. It works correctly for all cases (empty lines, all-whitespace lines, normal lines), but the double-negation logic (`[..^(trimmed.Length)]`) is non-obvious. A reader might misinterpret it as "everything except the trimmed part" when it's actually "everything that was trimmed away."

**Suggested fix**: Consider a clearer equivalent:
```csharp
var line = _buffer[_caretLine];
var indent = line[..(line.Length - line.TrimStart().Length)];
```

---

### BracketMatcher.cs — Bracket matching ignores string/comment context

The bracket matcher (`FindMatch`, `ScanForBracket`, `FindEnclosing`) performs character-level scanning without checking whether brackets appear inside strings, comments, or regex literals. This means `{` inside `"hello {world}"` or `# {comment}` will be treated as a real bracket.

**Risk**: Incorrect bracket match highlighting in files with brackets inside strings/comments. This is a known limitation and would require integrating with the syntax tokenizer to fix properly.

---

### EditorControl.cs — HandleTab: multi-line indent bypasses EndEdit but duplicates its logic

**Lines 1228–1273**

The multi-line indent/unindent path directly pushes an `IndentEntry` and manually replicates the post-edit bookkeeping (`_textVisualDirty`, `_bracketMatchDirty`, `InvalidateLineStatesFrom`, `_buffer.IsDirty`, `_cleanUndoDepth`). This is intentional (compact undo entries), but the duplicated bookkeeping is fragile — if `EndEdit` gains new steps, the multi-line indent path must be updated separately.

**Suggested fix**: Extract the shared bookkeeping (dirty flags, line state invalidation) into a small helper that both `EndEdit` and the indent path call.

---

### AppSettings.cs — Non-atomic settings save

**Lines 58–63**

```csharp
var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(SettingsPath, json);
```

`File.WriteAllText` is not atomic. If the app crashes mid-write (or the system loses power), the settings file could be truncated. This is lower-risk than file saves since settings are small, but `FileHelper.AtomicWriteText` exists in the same project and could be reused.

**Suggested fix**: Use `FileHelper.AtomicWriteText(SettingsPath, json, Encoding.UTF8)`.

---

### EditorControl.cs — IsCaretInsideString: fallback scanner doesn't handle backtick or escape edge cases consistently

**Lines 428–444**

The fallback string detection (when no syntax tokens are available) treats backtick `` ` `` as a string delimiter and handles `\\` escapes. However, it doesn't handle escaped quotes within backtick strings (`` \` ``), and single-character "strings" like `'x'` in C-like languages would incorrectly suppress auto-close for the character after the closing quote.

**Risk**: Low — this path only runs when no syntax grammar is active (plain text mode), where auto-close behavior is less critical.

---

### ColorTheme.cs — ParseBrush: magenta fallback for invalid hex values

**Lines 63–78**

```csharp
catch
{
    var fallback = new SolidColorBrush(Colors.Magenta);
    fallback.Freeze();
    return fallback;
}
```

Invalid hex values in theme JSON produce a magenta brush instead of failing. This is a reasonable fallback for robustness, but makes theme authoring errors hard to debug — the user sees random magenta patches with no indication which color key is wrong.

**Suggested fix**: Add a `Debug.WriteLine` with the invalid hex value and property name.

---

### SyntaxManager.cs — Tokenize allocates `bool[]` and `List<SyntaxToken>` per line

**Line 75–76**

```csharp
var claimed = new bool[line.Length];
var tokens = new List<SyntaxToken>();
```

Each `Tokenize` call allocates a fresh `bool[]` and `List<SyntaxToken>`. For a 100-character line rendered 60 times per viewport, this is ~6,000 small allocations per render pass. The token cache mitigates this (most lines hit the cache), but on initial load or after edits, many lines are tokenized in a burst.

**Risk**: Minor GC pressure during large file loads or bulk operations. Already measured via BenchmarkDotNet as acceptable.

---

## Summary

### Top 3 Most Urgent Issues

1. **HandlePaste clipboard failure causes unrecoverable data loss** (WARNING) — The selection is deleted before the clipboard is read. If the clipboard read fails, the deletion cannot be undone. Fix by reading the clipboard before modifying the buffer.

2. **RestoreWindowPosition null dereference** (WARNING) — Missing null checks on `WindowLeft`/`WindowTop` can crash the app on startup with certain settings files. Fix by adding `.HasValue` checks.

3. **EnsureDefaultThemes/Grammars unhandled I/O** (WARNING) — If `%AppData%` is inaccessible, the app crashes on startup before showing any UI. Fix by wrapping in try/catch.

### Systemic Patterns

- **Inconsistent buffer mutation**: Some code paths use `TextBuffer` mutation methods (which handle max-line-length tracking), while others directly assign via the indexer. The project's CLAUDE.md documents the rule to always use mutation methods, but `SelectionManager.DeleteSelection` and `HandlePaste` don't follow it. Consider making the `TextBuffer` indexer setter internal/private and routing all mutations through named methods.

- **Silent error swallowing**: Seven locations use bare `catch` blocks. While acceptable for a desktop app's resilience, adding `Debug.WriteLine` traces would make grammar/theme authoring errors visible during development.

- **Post-edit bookkeeping duplication**: The multi-line indent path (`HandleTab`) manually replicates the flags and invalidations that `EndEdit` performs. If future edits add new bookkeeping steps, this duplication could lead to bugs.

### Overall Risk Assessment

**Low risk**. The application has no network surface, no credential handling, and no shared state beyond the local filesystem. The most impactful finding is the HandlePaste data loss bug, which requires a specific failure condition (clipboard locked by another process during paste with active selection). The codebase is well-structured, with clear separation of concerns and consistent patterns. The buffer mutation inconsistency is the main systemic issue worth addressing to prevent future bugs as the codebase grows.
