# Performance Review — Volt Text Editor

**Date:** 2026-04-03
**Scope:** Full project — every `.cs`, `.xaml`, `.xaml.cs`, config, and resource file reviewed.

---

## Executive Summary

Volt already demonstrates strong performance awareness: GlyphRun rendering bypasses DWrite shaping, find-match highlighting uses binary search, syntax token caching with pruning avoids redundant tokenization, the scroll buffer avoids re-render on small scrolls, and text mutations use `AsSpan`-based `string.Concat` to minimize allocations.

The most impactful wins available are:

1. **Tokenizer regex iteration strategy** (SyntaxManager) — advancing by 1 character per rule generates excessive regex calls on long lines. Skipping past matches would cut tokenization work significantly.
2. **Per-line `bool[]` allocation in Tokenize** — a new `bool[line.Length]` array is allocated for every non-trivial line tokenized. A reusable buffer would eliminate thousands of allocations per render.
3. **Forced GC on tab close** — triple `GC.Collect(2, Forced)` blocks the UI thread for 50-200ms. Removing or deferring this is a near-zero-effort fix.
4. **Event handler leak on EditorControl** — `FontManager` event subscriptions are never unsubscribed, preventing GC of closed editor instances.

**Finding counts:**

| Impact | Total | Resolved | Acceptable | Remaining |
|--------|-------|----------|------------|-----------|
| High | 4 | 4 | 0 | 0 |
| Medium | 8 | 6 | 2 | 0 |
| Low | 5 | 2 | 3 | 0 |
| **Total** | **17** | **12** | **5** | **0** |

---

## Algorithmic & Data Structure Efficiency

### P1. Tokenizer regex iteration advances by 1 character (High) — RESOLVED
**File:** `Volt/Editor/SyntaxManager.cs:192-200`

**Current behavior:** `ApplyGrammarRules` finds all candidate matches by calling `rule.CompiledRegex.Match(line, searchFrom)` in a loop, advancing `searchFrom` by only 1 character after each match. For a 200-character line with 10 grammar rules, this can generate 2000+ regex `Match()` calls. The comment explains this is intentional to capture overlapping candidates for the priority-based claiming pass.

**Recommended change:** After a successful match, advance `searchFrom` to `match.Index + match.Length` instead of `match.Index + 1`. Overlapping matches at the same position are already handled by the priority sort — the only case where advancing by 1 matters is when a higher-priority rule should claim a position inside a lower-priority match, but the greedy claiming pass already handles this correctly since candidates are sorted by `(Start, Priority)`.

**Estimated impact:** High — tokenization is called for every visible line on every render. For a typical 80-char line with 5 rules, this reduces regex calls from ~400 to ~25 (one match per rule per actual token).

**Trade-offs:** Requires verification that no grammar relies on overlapping candidates from the same rule. Test with all bundled grammars (Perl, JSON, Markdown).

---

### P2. `bool[]` allocation per Tokenize call (High) — RESOLVED
**File:** `Volt/Editor/SyntaxManager.cs:63`

**Current behavior:** Every call to `Tokenize` that reaches the grammar-rules path allocates `new bool[line.Length]`. This array tracks which characters have been "claimed" by tokens. For a viewport of 50 lines averaging 80 characters, this is 50 allocations totalling ~4KB per render frame.

**Recommended change:** Use a `ThreadLocal<bool[]>` or instance-level reusable buffer. Grow the buffer when needed, clear with `Array.Clear` (which is a fast memset) before each use:
```csharp
private bool[] _claimedBuf = Array.Empty<bool>();

// In Tokenize:
if (_claimedBuf.Length < line.Length)
    _claimedBuf = new bool[line.Length * 2]; // grow with headroom
else
    Array.Clear(_claimedBuf, 0, line.Length);
```

**Estimated impact:** High — eliminates ~50 array allocations per render frame, reducing GC pressure in the hottest code path.

**Trade-offs:** Requires `SyntaxManager` to be used single-threaded per instance (already the case — each `EditorControl` calls through its own reference, and background precomputation is serialized via dispatcher).

---

### P3. FindManager uses linear scan for initial index (Medium) — RESOLVED
**File:** `Volt/Editor/FindManager.cs:38-47`

**Current behavior:** After building the sorted match list, `Search()` uses a linear loop to find the first match at or after the caret position. The match list is already sorted by `(Line, Col)`.

**Recommended change:** Use binary search:
```csharp
int lo = 0, hi = _matches.Count - 1;
while (lo <= hi)
{
    int mid = (lo + hi) / 2;
    var (ml, mc, _) = _matches[mid];
    if (ml < caretLine || (ml == caretLine && mc < caretCol))
        lo = mid + 1;
    else
        hi = mid - 1;
}
_currentIndex = lo < _matches.Count ? lo : 0;
```

**Estimated impact:** Medium — O(n) to O(log n). Noticeable when searching for common tokens (e.g., "e") in large files with 10,000+ matches. The full-document search scan (lines 24-34) dominates total time, but this still matters for interactive responsiveness after the scan completes.

**Trade-offs:** None — drop-in replacement, same semantics.

---

### P4. UndoManager.Push uses `RemoveAt(0)` (Medium) — RESOLVED
**File:** `Volt/Editor/UndoManager.cs:50-51`

**Current behavior:** When the undo stack exceeds `MaxEntries` (200), the oldest entry is removed via `_undoStack.RemoveAt(0)`, which shifts all remaining ~200 entries in memory.

**Recommended change:** Replace `List<UndoEntryBase>` with a circular buffer or `LinkedList<UndoEntryBase>`. A simple approach: use a `List` but track a logical start index, only compacting periodically. Alternatively, since `MaxEntries` is 200, the O(n) shift is ~200 reference copies — fast enough that this is unlikely to be perceptible.

**Estimated impact:** Medium in theory (O(n) per push at cap), but low in practice — 200 reference copies is ~1.6KB memcpy, sub-microsecond on modern hardware. Only worth fixing if profiling shows it matters.

**Trade-offs:** Circular buffer adds complexity. The current approach is simple and correct.

---

### P5. Token cache pruning enumerates all keys (Low) — ACCEPTABLE
**File:** `Volt/Editor/EditorControl.cs:1019-1030`

**Current behavior:** When the token cache exceeds 3x the viewport size, all dictionary keys are enumerated to find entries outside the viewport window. Uses a reusable `_pruneKeys` list to avoid per-prune allocation (good).

**Recommended change:** The current implementation is already well-guarded (only triggers when cache exceeds 3x viewport, uses reusable list). A `SortedDictionary` would allow range-based pruning, but insertion cost increases. Not worth changing unless profiling shows this is a bottleneck.

**Estimated impact:** Low — the guard condition prevents this from running on most frames, and dictionary key enumeration of ~150-300 entries is fast.

**Trade-offs:** `SortedDictionary` has O(log n) insertion vs O(1) for `Dictionary`, which matters more since insertion happens every frame.

---

## Memory & Resource Management

### P6. Forced GC blocks UI thread on tab close (High) — RESOLVED
**File:** `Volt/UI/MainWindow.xaml.cs:219-221`

**Current behavior:** After closing a tab with a large file, the code calls:
```csharp
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
```
This blocks the UI thread for 50-200ms depending on heap size. The `blocking: true` parameter ensures the calling thread waits for collection to complete.

**Recommended change:** Either remove entirely (let the runtime GC handle it — it will collect when memory pressure warrants), or schedule it on a background thread after a delay:
```csharp
if (wasLarge)
    Task.Delay(500).ContinueWith(_ => GC.Collect(2, GCCollectionMode.Optimized));
```

**Estimated impact:** High — eliminates a 50-200ms UI freeze every time a large file tab is closed. Users closing multiple tabs in sequence experience cumulative blocking.

**Trade-offs:** Memory may remain allocated slightly longer before being returned to the OS. For a desktop editor, this is acceptable — the runtime GC will collect eventually.

---

### P7. EditorControl FontManager event subscriptions never unsubscribed (High) — RESOLVED
**File:** `Volt/Editor/EditorControl.cs:227-228, 264-269`

**Current behavior:** The constructor subscribes to `_font.BeforeFontChanged` and `_font.FontChanged`. The `Unloaded` handler unsubscribes from `ThemeManager.ThemeChanged` and stops the blink timer, but never unsubscribes from the font events. The `_buffer.DirtyChanged` lambda (line 233) also creates a closure that captures `this`, preventing GC.

**Recommended change:** Add font event cleanup to the `Unloaded` handler:
```csharp
Unloaded += (_, _) =>
{
    _blinkTimer.Stop();
    _font.BeforeFontChanged -= OnBeforeFontChanged;
    _font.FontChanged -= OnFontChanged;
    if (ThemeManager != null)
        ThemeManager.ThemeChanged -= OnThemeChanged;
};
```

**Estimated impact:** High — without cleanup, every closed tab leaks an `EditorControl` instance (with its `TextBuffer`, `UndoManager`, token cache, etc.) for the lifetime of the `FontManager`. A user opening and closing 20 tabs accumulates 20 dead `EditorControl` instances in memory.

**Trade-offs:** None — this is a straightforward bug fix.

---

### P8. Token cache stores duplicate line strings (Medium) — ACCEPTABLE
**File:** `Volt/Editor/EditorControl.cs:990-994`

**Current behavior:** The token cache stores `(string content, LineState inState, List<SyntaxToken> tokens)` per line. The `content` field is a reference to the same string in `_buffer[i]`, so it's not a copy — but after edits, the old string remains in the cache until pruned, preventing its collection.

**Recommended change:** Use a hash or version counter instead of string reference equality for cache invalidation. When a line is edited, `_buffer[i]` gets a new string reference, so the old string in the cache becomes garbage but isn't collected until pruning runs. Adding an edit generation counter to `TextBuffer` (incremented on any mutation) would allow O(1) staleness detection.

**Estimated impact:** Medium — for large files with frequent edits, stale cache entries hold onto old string references. The existing pruning logic bounds this to 3x viewport size, limiting the damage.

**Trade-offs:** Generation-counter approach requires plumbing a counter through `TextBuffer` and the cache. The current string-reference comparison is simple and correct.

---

### P9. `new string(' ', spaces)` in ApplyIndentEntry (Low) — RESOLVED
**File:** `Volt/Editor/EditorControl.cs:456`

**Current behavior:** During undo/redo of indent operations, `new string(' ', spaces)` is called per line. For a 100-line indent operation, this creates 100 small strings.

**Recommended change:** Pre-allocate a set of common indent strings (1-8 spaces) and reuse them. Not worth the complexity for typical usage.

**Estimated impact:** Low — undo/redo is infrequent and operates on small line counts.

**Trade-offs:** Not worth the added complexity.

---

## Concurrency & Async

### P10. Background line-state precomputation is well-designed (Positive)
**File:** `Volt/Editor/EditorControl.cs:705-743`

`EnsureLineStates` uses convergence optimization — revalidation stops early when output state matches cached value. The background precomputation at idle priority with generation-based cancellation is a good design that avoids blocking the UI thread.

No changes needed.

---

## I/O and File Operations

### P11. ExplorerTreeControl creates FormattedText objects per row per render (Medium) — RESOLVED
**File:** `Volt/UI/ExplorerTreeControl.cs:260-277`

**Current behavior:** The `OnRender` method creates new `FormattedText` objects for arrow chevrons and file/folder icons on every render pass, for every visible row. With ~50 visible rows, this is ~100 `FormattedText` allocations per render.

**Recommended change:** Cache `FormattedText` objects for the fixed set of glyphs (chevron-right, chevron-down, file icon, folder icon). These only change when theme or DPI changes:
```csharp
private FormattedText? _cachedChevronRight, _cachedChevronDown, _cachedFileIcon, _cachedFolderIcon;
```
Invalidate the cache on theme change and DPI change.

**Estimated impact:** Medium — eliminates ~100 `FormattedText` allocations per explorer render. The existing comment notes FormattedText is acceptable for ~50 rows, but caching the fixed glyphs is trivial and removes unnecessary work.

**Trade-offs:** Minimal — 4 cached fields, invalidated on theme/DPI change.

---

### P12. Synchronous directory loading in FileTreeItem (Medium) — RESOLVED
**File:** `Volt/UI/FileTreeItem.cs:84-112`

**Current behavior:** `LoadChildren` enumerates directory contents synchronously on the UI thread. For folders with thousands of files, this blocks the UI.

**Recommended change:** Load children asynchronously, or at minimum defer the sort/filter to a background task and marshal results back to the UI thread. Show a loading indicator for large directories.

**Estimated impact:** Medium — folders with 1000+ items cause perceptible UI hangs (100-500ms). Typical project folders are smaller, but `node_modules` or build output directories can be large.

**Trade-offs:** Requires async pattern; adds complexity. Could be mitigated by setting a maximum child count with a "show more" mechanism.

---

### P13. Session restore reads files multiple times (Low) — ACCEPTABLE
**File:** `Volt/UI/MainWindow.xaml.cs:502-588`

**Current behavior:** During session restore, file verification (existence check, size check, tail-byte verification) and content reading are separate operations. Each tab's file is touched multiple times.

**Recommended change:** Combine file verification and content reading into a single pass. Open the file once, read size from the stream, verify tail bytes, and read content in one operation.

**Estimated impact:** Low — session restore happens once at startup with typically 1-5 tabs. The overhead is a few extra file opens, measured in low milliseconds.

**Trade-offs:** Minimal — straightforward refactoring.

---

## Rendering Performance

### P14. Line number `ToString()` allocation in gutter render (Medium) — RESOLVED
**File:** `Volt/Editor/EditorControl.cs:1096`

**Current behavior:** `(i + 1).ToString()` is called for every visible line in `RenderGutterVisual`. For 50 visible lines, this creates 50 small string allocations per render frame.

**Recommended change:** Cache line number strings in a dictionary or array. Line numbers change rarely (only when lines are added/removed or viewport scrolls), and the same numbers are rendered frame after frame:
```csharp
private readonly Dictionary<int, string> _lineNumStrings = new();

private string GetLineNumString(int lineNum)
{
    if (!_lineNumStrings.TryGetValue(lineNum, out var s))
    {
        s = lineNum.ToString();
        _lineNumStrings[lineNum] = s;
    }
    return s;
}
```
Clear the cache on `SetContent` (new file loaded).

**Estimated impact:** Medium — eliminates 50 string allocations per render frame. Each allocation is small (1-6 chars), but they accumulate and contribute to GC pressure during smooth scrolling.

**Trade-offs:** Dictionary lookup overhead vs string allocation. Net positive for scrolling performance.

---

### P15. Block caret `char.ToString()` in UpdateCaretVisual (Low) — RESOLVED
**File:** `Volt/Editor/EditorControl.cs:307`

**Current behavior:** `_buffer[_caretLine][_caretCol].ToString()` creates a new single-character string every caret blink (every 500ms). The string is passed to `DrawGlyphRun`.

**Recommended change:** Cache the single-character string when caret position changes, not on every blink. Or add a `DrawGlyphRun` overload that accepts a `char` directly.

**Estimated impact:** Low — 2 allocations per second is negligible. Only worth addressing if `DrawGlyphRun` is refactored for other reasons.

**Trade-offs:** Not worth the complexity on its own.

---

### P16. Command palette filtering allocates new lists per keystroke (Medium) — RESOLVED
**File:** `Volt/UI/CommandPalette.xaml.cs:223-236`

**Current behavior:** `GetFilteredCommands()` and `GetFilteredOptions()` call `.Where(...).ToList()` on every keystroke, allocating a new `List<T>` each time. The command list is typically small (20-30 items).

**Recommended change:** For such a small list, this is acceptable. If command count grows, consider reusing a `List<T>` and calling `.Clear()` + manual population instead of LINQ. Alternatively, use `List<T>.FindAll()` which is slightly more efficient than `Where().ToList()`.

**Estimated impact:** Medium in principle, but low in practice — the list is small (~30 items) and typing speed is limited to ~10 keystrokes/second. The real cost is the `Items.Clear()` + re-add loop in `RefreshList()` (line 240+) which rebuilds WPF ListBox items.

**Trade-offs:** Current code is readable and correct. Only optimize if palette responsiveness becomes an issue.

---

### P17. `List<SyntaxToken>` allocation per Tokenize call (Medium) — ACCEPTABLE
**File:** `Volt/Editor/SyntaxManager.cs:64`

**Current behavior:** Every `Tokenize` call allocates `new List<SyntaxToken>()`. For 50 visible lines per render, this is 50 list allocations (plus internal array resizing as tokens are added).

**Recommended change:** Accept a caller-provided list that gets cleared and reused, or use an `ArrayPool<SyntaxToken>`-backed buffer. Since `Tokenize` returns the list and it gets stored in the token cache, the caller would need to copy out the results before reuse.

**Estimated impact:** Medium — reduces list allocations from ~50/frame to 0 in the steady-state case (when cache hits dominate). On cache misses (scrolling to new content), still allocates since the list is stored in the cache.

**Trade-offs:** API change required. The cache stores the list reference, so a pooling approach would need to clone for cache storage. Net benefit is marginal given the cache hit rate.

---

## Summary Table

| # | Severity | Category | File | Description |
|---|----------|----------|------|-------------|
| P1 | High | Algorithm | SyntaxManager.cs:192-200 | Regex iteration advances by 1 char instead of match length | **RESOLVED** |
| P2 | High | Memory | SyntaxManager.cs:63 | `bool[]` allocated per Tokenize call | **RESOLVED** |
| P6 | High | Memory | MainWindow.xaml.cs:219-221 | Forced GC blocks UI thread on tab close | **RESOLVED** |
| P7 | High | Memory | EditorControl.cs:227-228 | FontManager event subscriptions never unsubscribed (leak) | **RESOLVED** |
| P3 | Medium | Algorithm | FindManager.cs:38-47 | Linear scan for initial match index | **RESOLVED** |
| P4 | Medium | Algorithm | UndoManager.cs:50-51 | `RemoveAt(0)` shifts 200 entries | **RESOLVED** |
| P8 | Medium | Memory | EditorControl.cs:990-994 | Stale strings in token cache after edits | ACCEPTABLE |
| P11 | Medium | Rendering | ExplorerTreeControl.cs:260-277 | FormattedText created per row per render | **RESOLVED** |
| P14 | Medium | Rendering | EditorControl.cs:1096 | `ToString()` per line number per render | **RESOLVED** |
| P16 | Medium | Rendering | CommandPalette.xaml.cs:223-236 | LINQ `.ToList()` per keystroke | **RESOLVED** |
| P17 | Medium | Memory | SyntaxManager.cs:64 | `List<SyntaxToken>` allocated per Tokenize | ACCEPTABLE |
| P12 | Medium | I/O | FileTreeItem.cs:84-112 | Synchronous directory loading blocks UI | **RESOLVED** |
| P5 | Low | Algorithm | EditorControl.cs:1019-1030 | Token cache pruning enumerates all keys | ACCEPTABLE |
| P9 | Low | Memory | EditorControl.cs:456 | `new string(' ', spaces)` in indent undo | **RESOLVED** |
| P13 | Low | I/O | MainWindow.xaml.cs:502-588 | Session restore touches files multiple times | ACCEPTABLE |
| P15 | Low | Rendering | EditorControl.cs:307 | `char.ToString()` on every caret blink | **RESOLVED** |

---

## Quick Wins (Best Effort-to-Impact Ratio)

1. **P6 — Remove forced GC on tab close** — Delete 3 lines. Immediate UI responsiveness improvement. Zero risk.

2. **P7 — Unsubscribe font events in Unloaded** — Add 2 lines to existing handler. Fixes a memory leak. Zero risk.

3. **P2 — Reuse `bool[]` buffer in Tokenize** — Add one field, replace `new bool[]` with `Array.Clear`. Eliminates ~50 allocations per render. Minimal risk.

4. **P1 — Advance regex past match in ApplyGrammarRules** — Change `searchFrom = match.Index + 1` to `searchFrom = match.Index + match.Length`. Single-line change with major reduction in regex calls. Requires testing with all grammars.

5. **P14 — Cache line number strings** — Small dictionary cache. Eliminates 50 string allocations per render during scrolling.

6. **P3 — Binary search for FindManager initial index** — Replace 8-line linear loop with 8-line binary search. O(n) to O(log n) for large match counts.

7. **P11 — Cache FormattedText for explorer glyphs** — 4 cached fields for fixed icon/arrow glyphs. Eliminates ~100 allocations per explorer render.
