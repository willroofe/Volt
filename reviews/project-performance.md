# TextEdit Performance Review

**Date:** 2026-04-01
**Codebase Size:** ~5,100 lines of C# across 16 source files, plus ~1,200 lines of XAML
**Context:** WPF desktop application — a custom text editor with direct `DrawingContext`/`GlyphRun` rendering, JSON-based syntax highlighting, and monospace font enumeration. Not a server application; performance matters most in rendering, keystroke handling, and large-file editing.

---

## SIGNIFICANT

### ~~FontManager.cs -- `DrawGlyphRun` allocates two arrays per call (lines 100-101)~~ ADDRESSED

Every call to `DrawGlyphRun` allocates `ushort[length]` and `double[length]` for glyph indices and advance widths. This method is called **per-token per-line** in the render loop, meaning a typical frame rendering ~80 visible lines with ~5 tokens each produces ~400+ array allocations. GC pressure from short-lived arrays in a hot rendering path is a known WPF performance concern.

```csharp
// FontManager.cs:100-101
var glyphIndices = new ushort[length];
var advanceWidths = new double[length];
```

**Impact:** SIGNIFICANT during scrolling and typing on large files. Each keystroke triggers a re-render that allocates hundreds of small arrays, increasing Gen0 GC frequency.

**Suggested fix:** Pool or reuse a pair of scratch arrays at the `FontManager` level, resizing only when length exceeds the current capacity:

```csharp
private ushort[] _glyphBuf = new ushort[256];
private double[] _advBuf = new double[256];

public void DrawGlyphRun(DrawingContext dc, string text, int startIndex, int length, ...)
{
    if (length <= 0) return;
    if (length > _glyphBuf.Length)
    {
        _glyphBuf = new ushort[length];
        _advBuf = new double[length];
    }
    // fill _glyphBuf[0..length], _advBuf[0..length] ...
    var run = new GlyphRun(..., _glyphBuf.AsSpan(0, length).ToArray(), ...);
    // Note: GlyphRun stores the array reference, so a final ToArray() is needed,
    // but the scratch buffers eliminate per-call allocation of the advance widths
    // since we can set advanceWidths to null when all widths are CharWidth (monospace).
}
```

~~Implemented: Cached a uniform `double[]` advance widths array (pre-filled with `CharWidth`) and pass it to `GlyphRun` via `ArraySegment<double>` — eliminates the `double[]` allocation entirely. Benchmark result: per-frame allocation dropped from 184 KB to 137 KB (-25%), with individual token allocation dropping up to 71% (5,600 B to 1,608 B for 500-char lines).~~

---

### SyntaxManager.cs -- `ApplyGrammarRules` runs every regex from every rule against every line (lines 158-190)

For each line, `ApplyGrammarRules` iterates all grammar rules and calls `Match()` in a loop advancing by 1 position. With Perl's grammar having ~20 rules, each line may invoke ~20 regex engines, each scanning from multiple start positions. On a 200-character line with 20 rules, this is roughly 20 × 200 = 4,000 regex `Match()` calls per line in the worst case.

```csharp
// SyntaxManager.cs:165-174
for (int r = 0; r < _activeGrammar!.Rules.Count; r++)
{
    var rule = _activeGrammar.Rules[r];
    ...
    int searchFrom = ruleStart;
    while (searchFrom < line.Length)
    {
        var match = rule.CompiledRegex.Match(line, searchFrom);
        if (!match.Success) break;
        if (match.Length > 0)
            candidates.Add((r, match.Index, match.Length, rule.Scope));
        searchFrom = match.Index + 1;
    }
}
```

**Impact:** SIGNIFICANT for large files with long lines (e.g. minified code). The 50ms regex timeout in `SyntaxDefinition.Compile()` provides a safety net against catastrophic backtracking, but the sheer number of regex invocations is the bottleneck. Background precomputation at idle priority mitigates this for initial load, but it still impacts every keystroke that dirties the token cache for visible lines.

**Suggested fix:** Use `Matches()` instead of the advancing `Match()` loop — .NET's `Regex.Matches()` returns all non-overlapping matches in a single pass, which is significantly faster than repeatedly calling `Match(line, searchFrom)` with `searchFrom = match.Index + 1`. The current approach generates overlapping candidates that are later de-duplicated by the claiming pass, but overlapping matches from a single rule are almost always redundant:

```csharp
foreach (Match match in rule.CompiledRegex.Matches(line, ruleStart))
{
    if (match.Length > 0)
        candidates.Add((r, match.Index, match.Length, rule.Scope));
}
```

This reduces per-rule work from O(n) `Match()` calls to a single `Matches()` call. The comment at line 161 says advancing-by-1 is needed for overlapping matches, but since the claiming pass is greedy (first-match-at-position wins), overlapping candidates from the same rule at offsets +1, +2, etc. are always discarded. Verify by testing with the Perl grammar.

**Benchmark result:** Switching to `Matches()` was a **net regression** — the `MatchCollection` + enumerator overhead per rule (×20 rules) outweighed the reduced match count. Simple line went from 3.8 us / 1.44 KB to 4.5 us / 4.53 KB. Change was reverted. The existing `Match()` loop is the better approach for this workload.

---

### ~~SyntaxManager.cs -- `DetectUnclosedString` allocates a second `bool[line.Length]` array (line 254)~~ ADDRESSED

`DetectUnclosedString` creates its own `tokenized[]` array from the token list, duplicating work already done by the `claimed[]` array in the calling `Tokenize` method. The `claimed[]` array tracks exactly which positions are covered by tokens.

```csharp
// SyntaxManager.cs:251-255
private static LineState DetectUnclosedString(string line, List<SyntaxToken> tokens)
{
    var tokenized = new bool[line.Length];
    foreach (var t in tokens)
        for (int i = t.Start; i < t.Start + t.Length && i < line.Length; i++)
            tokenized[i] = true;
```

**Impact:** MODERATE. This allocates a `bool[]` + fills it in O(n) for every line tokenized. Not a bottleneck alone, but it compounds with the per-line overhead in `Tokenize` (which already allocates `claimed[]`).

~~Implemented: Changed `DetectUnclosedString` to accept `bool[] claimed` directly instead of `List<SyntaxToken> tokens`. Eliminates the `bool[line.Length]` allocation and O(tokens) fill loop. Benchmark result: small allocation reduction per line (1.44 KB to 1.38 KB on simple lines, 54.09 KB to 53.41 KB on long lines).~~

---

## MODERATE

### ~~TextBuffer.cs -- `MaxLineLength` does `_lines.Max(l => l.Length)` via LINQ (line 46)~~ ADDRESSED

When `_maxLineLengthDirty` is true, computing the max scans every line. This is O(n) where n = line count. On a 100K-line file this iterates 100K strings. The flag is set by many operations (InsertLine, RemoveAt, JoinWithNext, etc.) and cleared on next access.

```csharp
// TextBuffer.cs:46
_maxLineLength = _lines.Count > 0 ? _lines.Max(l => l.Length) : 0;
```

**Impact:** MODERATE. Usually the invalidation is followed by `UpdateExtent()` which accesses `MaxLineLength`, so the scan happens once per edit. But after operations like `ReplaceAll` that hit many lines, or `SetContent` on a large file, this could spike.

**Suggested fix:** Replace LINQ `.Max()` with a simple loop to eliminate the delegate allocation and IEnumerator overhead:

```csharp
int max = 0;
for (int i = 0; i < _lines.Count; i++)
    if (_lines[i].Length > max) max = _lines[i].Length;
_maxLineLength = max;
```

~~Implemented: Replaced LINQ `.Max()` with a `for` loop. Benchmark result: 100K lines 227 us -> 86 us (-62%), 1K lines 2.2 us -> 0.66 us (-70%). Zero allocations in both cases.~~

---

### ~~EditorControl.cs -- `OnRender` iterates ALL find matches to draw highlights (lines 667-681)~~ ADDRESSED

The find match rendering loop checks every match in the document against the visible line range:

```csharp
// EditorControl.cs:667-681
for (int m = 0; m < _find.Matches.Count; m++)
{
    var (mLine, mCol, mLen) = _find.Matches[m];
    if (mLine < firstLine || mLine > lastLine) continue;
    ...
}
```

**Impact:** MODERATE. If a common search term matches 50,000 times in a large file, every render frame scans all 50K matches even though only ~80 are visible. The inner body is cheap (skip + draw rectangle), but the loop overhead is linear in total match count.

**Suggested fix:** Since `_find.Matches` is sorted by line, use binary search to find the first match at `firstLine` and iterate only until past `lastLine`:

```csharp
int lo = 0, hi = _find.Matches.Count - 1;
while (lo <= hi) { int mid = (lo + hi) / 2; if (_find.Matches[mid].Line < firstLine) lo = mid + 1; else hi = mid - 1; }
for (int m = lo; m < _find.Matches.Count; m++)
{
    var (mLine, mCol, mLen) = _find.Matches[m];
    if (mLine > lastLine) break;
    // draw...
}
```

~~Implemented: Binary search to find first visible match, then iterate only until past `lastLine`. O(log n + visible) instead of O(total matches).~~

---

### ~~EditorControl.cs -- `RenderTextVisual` token cache pruning allocates a key list (lines 788-793)~~ ADDRESSED

After rendering, the token cache is pruned by collecting keys to remove into a new `List<int>`, then removing them:

```csharp
// EditorControl.cs:788-793
var keysToRemove = new List<int>();
foreach (var key in _tokenCache.Keys)
    if (key < pruneBelow || key > pruneAbove)
        keysToRemove.Add(key);
foreach (var key in keysToRemove)
    _tokenCache.Remove(key);
```

**Impact:** MODERATE. Allocates a list on every text re-render when the cache exceeds the threshold. The pruning guard (`_tokenCache.Count > (drawLast - drawFirst + 1) * 3`) limits frequency, but each occurrence allocates.

**Suggested fix:** Avoid the intermediate list by iterating over `_tokenCache` in a removal-safe way, or switch to a simpler approach: clear the entire cache and let it rebuild on demand (the render loop just ran, so the visible lines will be re-populated immediately). Since the token cache is only consulted during rendering:

```csharp
// Simpler: just clear stale entries directly
_tokenCache.Keys.Where(k => k < pruneBelow || k > pruneAbove)
    .ToList().ForEach(k => _tokenCache.Remove(k));
```

~~Implemented: Added a reusable `_pruneKeys` list field. The list is cleared and reused each prune, eliminating per-render allocation.~~

---

### ~~TextBuffer.cs -- `InsertAt`, `DeleteAt`, `ReplaceAt` create intermediate strings via slicing (lines 141-158)~~ ADDRESSED

Every character insertion, deletion, or replacement creates 2-3 intermediate string slices that are immediately concatenated:

```csharp
// TextBuffer.cs:144
_lines[line] = _lines[line][..col] + text + _lines[line][col..];
```

**Impact:** MODERATE for rapid typing on very long lines (e.g. 10K+ characters). Each keystroke allocates the left slice, the right slice, and the concatenated result. For typical line lengths (<200 chars) this is fast enough. For extreme lines it could become noticeable.

**Suggested fix:** Use `string.Create` or `StringBuilder` for the 3-part concatenation:

```csharp
_lines[line] = string.Concat(_lines[line].AsSpan(0, col), text, _lines[line].AsSpan(col));
```

~~Implemented: All three methods now use `string.Concat` with `AsSpan`. Benchmark result: 80-char InsertAt 392 B -> 184 B (-53%), 33 ns -> 15 ns (-56%); 10K-char InsertAt 60 KB -> 40 KB (-33%), 1,920 ns -> 1,265 ns (-34%).~~

---

### ~~EditorControl.cs -- `IsCaretInsideString` re-tokenizes the current line on every character input (line 369)~~ ADDRESSED

Each `OnTextInput` call invokes `IsCaretInsideString` which calls `SyntaxManager.Tokenize(line, inState, out _)`. This re-tokenizes the current line even though the render loop will tokenize it again moments later.

```csharp
// EditorControl.cs:365-369
private bool IsCaretInsideString(string line, int caretCol)
{
    EnsureLineStates(_caretLine);
    var inState = _caretLine < _lineStates.Count ? _lineStates[_caretLine] : SyntaxManager.DefaultState;
    var tokens = SyntaxManager.Tokenize(line, inState, out _);
```

**Impact:** MODERATE. Doubles the tokenization work for each character typed. Tokenization is the most expensive per-line operation in the editor.

**Suggested fix:** Check the token cache first — if the current line's tokens are already cached (from the most recent render), use those directly instead of re-tokenizing:

```csharp
private bool IsCaretInsideString(string line, int caretCol)
{
    List<SyntaxToken> tokens;
    EnsureLineStates(_caretLine);
    var inState = _caretLine < _lineStates.Count ? _lineStates[_caretLine] : SyntaxManager.DefaultState;
    if (_tokenCache.TryGetValue(_caretLine, out var cached) && cached.content == line && cached.inState == inState)
        tokens = cached.tokens;
    else
        tokens = SyntaxManager.Tokenize(line, inState, out _);
    ...
}
```

~~Implemented: Check `_tokenCache` for a valid cached entry before calling `SyntaxManager.Tokenize`. Avoids redundant tokenization on every keystroke when the cache has the answer from the most recent render.~~

---

## MINOR

### FontManager.cs -- `GetMonospaceFonts` creates two `FormattedText` objects per system font (lines 133-136)

`FormattedText` is one of the heavier WPF text APIs. Enumerating ~300 system fonts creates ~600 `FormattedText` instances. This runs once (cached), and is deferred to idle priority, so user-facing impact is minimal.

```csharp
// FontManager.cs:133-136
var narrow = new FormattedText("i", ...);
var wide = new FormattedText("M", ...);
```

**Impact:** MINOR. One-time cost at idle priority. Takes ~200-500ms depending on installed fonts.

**Suggested fix:** No change needed. The idle-priority dispatch and caching are already correct. If this ever needs to be faster, `GlyphTypeface.AdvanceWidths` could compare glyph metrics directly without `FormattedText`.

---

### SyntaxManager.cs -- `tokens.Sort()` on every tokenized line (line 92)

After tokenization, the token list is sorted by start position. Most of the time tokens are already approximately sorted by start position since grammar rules process left-to-right.

```csharp
// SyntaxManager.cs:92
tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
```

**Impact:** MINOR. A `List.Sort` on a nearly-sorted list of ~5-15 items is effectively free (TimSort-like behavior). Only notable on pathological lines with many tokens.

**Suggested fix:** No change needed. The cost is negligible.

---

### SyntaxManager.cs -- `"msixpodualngcer".Contains(line[endPos])` for regex flag scanning (lines 140, 367, 479)

Single-character `string.Contains(char)` is O(n) on the flag string for each flag character scanned. This is called during regex close detection.

```csharp
// SyntaxManager.cs:140
while (endPos < line.Length && "msixpodualngcer".Contains(line[endPos]))
    endPos++;
```

**Impact:** MINOR. The flag string is 15 characters and regex flags are rare/short (1-4 chars). Total work: ~60 comparisons at most.

**Suggested fix:** No change needed. A `HashSet<char>` or `switch` would be microscopically faster but less readable.

---

### UndoManager.cs -- `_undoStack.RemoveAt(0)` for eviction (line 35)

When the undo stack exceeds 200 entries, the oldest is removed from index 0, which is O(n) on a `List<T>` due to the array shift.

```csharp
// UndoManager.cs:35
_undoStack.RemoveAt(0);
```

**Impact:** MINOR. n = 200 (MaxEntries), so it shifts 199 references. This happens at most once per edit action and only after 200 edits accumulate. Negligible for a desktop editor.

**Suggested fix:** No change needed. A `LinkedList` or ring buffer would be O(1) eviction but adds complexity for negligible gain at this stack size.

---

### EditorControl.cs -- `RenderGutterVisual` calls `(i + 1).ToString()` per line (line 818)

Line number rendering allocates a string per visible line for the number-to-string conversion:

```csharp
// EditorControl.cs:818
var numStr = (i + 1).ToString();
```

**Impact:** MINOR. ~80 small string allocations per render. These are very small (1-6 chars) and collected quickly.

**Suggested fix:** No change needed. `.ToString()` on small ints is efficiently cached for 0-9 in modern .NET. A `Span<char>` + `TryFormat` approach would eliminate allocations but adds complexity for marginal gain.

---

## Summary

### Overall Performance Score: **4 / 5**

**Justification:** The architecture is well-optimized for a custom text editor: layered `DrawingVisual` rendering, region-based undo, render buffer with transform-based scrolling, token caching with viewport-aware pruning, and background line state precomputation. The `GlyphRun` approach over `FormattedText` is the right call for scroll performance. The convergence optimization in `EnsureLineStates` is particularly clever — it stops revalidating syntax states early when consecutive lines converge. The monospace font cache is correctly deferred to idle priority. These are strong engineering choices.

The main performance liabilities are allocation-heavy patterns in the hottest paths: `DrawGlyphRun` arrays (hundreds of allocations per frame), `ApplyGrammarRules` running many regex passes per line, and the redundant tokenization in `IsCaretInsideString`. None of these will cause visible stuttering on typical files (<10K lines), but they compound as file size increases.

### Top 3 Highest-Impact Changes

1. ~~**Pool/optimize `DrawGlyphRun` arrays**~~ **DONE** — Cached uniform advance widths via `ArraySegment<double>`, eliminating one of two per-call allocations. Per-frame allocation: 184 KB -> 137 KB (-25%). Individual 500-char calls: 5,600 B -> 1,608 B (-71%).

2. **~~Switch `ApplyGrammarRules` from advancing `Match()` to `Matches()`~~** **REVERTED** — Benchmarking showed `Matches()` adds `MatchCollection` + enumerator overhead per rule that outweighs the benefit. The existing `Match()` loop is faster for this workload.

3. **Cache-check in `IsCaretInsideString` before re-tokenizing** — Avoids re-tokenizing the current line on every keystroke when the token cache already has the answer. One-line fix with immediate benefit.

### Patterns That Would Become Bottlenecks at Scale

- **Find match rendering at O(total_matches)**: A 500K-line file with a common search term could produce 100K+ matches. The linear scan in `OnRender` would dominate frame time. Binary search is the fix.
- **`MaxLineLength` full scan**: After operations touching many lines (e.g. large paste, ReplaceAll), the O(n) LINQ scan runs. On a 500K-line file this is notable.
- **String slicing in `InsertAt`/`DeleteAt`**: On extremely long lines (10K+ chars), the intermediate string allocations per keystroke become measurable. `string.Concat(ReadOnlySpan<char>, ...)` eliminates them.

### Architectural Observations

No architectural changes are needed. The application's design is already well-suited to its domain:
- The layered `DrawingVisual` approach with render buffer + transform scrolling is the correct WPF pattern for a scrollable editor.
- Background line state precomputation at idle priority is the right strategy for large files.
- Token caching with generation-based invalidation prevents redundant work.
- Region-based undo avoids full-document snapshots.

The main opportunity is reducing per-frame GC pressure in the rendering pipeline, which is a localized optimization rather than an architectural concern.
