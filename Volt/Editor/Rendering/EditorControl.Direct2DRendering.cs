using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2DBitmapOptions = Vortice.Direct2D1.BitmapOptions;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using D2DRect = Vortice.Mathematics.Rect;
using D3D9Format = Vortice.Direct3D9.Format;
using D3D9Usage = Vortice.Direct3D9.Usage;
using D3D11Format = Vortice.DXGI.Format;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;
using RawRectF = Vortice.RawRectF;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct2D1.D2D1;
using static Vortice.Direct3D11.D3D11;
using static Vortice.Direct3D9.D3D9;
using static Vortice.DirectWrite.DWrite;

namespace Volt;

public partial class EditorControl
{
    private bool _wpfLayersClearedForDirect2D;
    private int _direct2DTargetVersion = -1;

    private bool TryRenderDirect2DFrame(int firstLine, int lastLine, bool longLineScrolled, bool perfEnabled, long frameStart)
    {
        if (_activeRenderMode != EditorRenderMode.Direct2D || _direct2DRenderer is not { IsAvailable: true } renderer)
            return false;

        int pixelWidth = Math.Max(1, (int)Math.Ceiling(Math.Max(1, ActualWidth) * _font.Dpi));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(Math.Max(1, ActualHeight) * _font.Dpi));
        if (!renderer.EnsureTarget(pixelWidth, pixelHeight, _font.Dpi, out var targetReason))
        {
            FallbackToWpfRenderer(targetReason ?? "Direct2D target creation failed");
            return false;
        }
        if (renderer.TargetVersion != _direct2DTargetVersion)
        {
            _direct2DTargetVersion = renderer.TargetVersion;
            _textVisualDirty = true;
            _gutterVisualDirty = true;
        }

        LogRendererStateIfNeeded(pixelWidth, pixelHeight);
        EnsureDirect2DVisual();
        ClearWpfLayerVisualsForDirect2D();

        bool rebuiltText = false;
        bool rebuiltGutter = false;
        bool rebuiltCaret = false;
        int glyphRuns = 0;
        int staticLayerRebuilds = 0;
        double textMs = 0;
        double gutterMs = 0;
        double decorationMs = 0;
        double caretMs = 0;

        if (TextViewportNeedsRefresh(firstLine, lastLine, longLineScrolled))
        {
            long textStart = StartPerfTimer(perfEnabled);
            glyphRuns += RebuildDirect2DTextLayer(renderer, firstLine, lastLine);
            textMs = StopPerfTimer(perfEnabled, textStart);
            _textVisualDirty = false;
            rebuiltText = true;
            staticLayerRebuilds++;
        }

        if (GutterViewportNeedsRefresh(firstLine, lastLine))
        {
            long gutterStart = StartPerfTimer(perfEnabled);
            glyphRuns += RebuildDirect2DGutterLayer(renderer, firstLine, lastLine);
            gutterMs = StopPerfTimer(perfEnabled, gutterStart);
            _gutterVisualDirty = false;
            rebuiltGutter = true;
            staticLayerRebuilds++;
        }

        long decorationsStart = StartPerfTimer(perfEnabled);
        int selectionRects;
        try
        {
            renderer.BeginPresent(ThemeManager.EditorBg);
            selectionRects = RenderDirect2DDecorations(renderer, firstLine, lastLine);
            renderer.DrawStaticLayers(
                Matrix3x2.CreateTranslation(
                    (float)-(_offset.X - _textXBias),
                    (float)-(_offset.Y - _textYBias)),
                new RawRectF((float)_gutterWidth, 0, (float)Math.Max(_gutterWidth, ActualWidth), (float)Math.Max(0, ActualHeight)),
                Matrix3x2.CreateTranslation(0, (float)-(_offset.Y - _gutterYBias)));
        }
        catch (Exception ex) when (renderer.MarkUnavailable(ex.Message))
        {
            FallbackToWpfRenderer(ex.Message);
            return false;
        }
        decorationMs = StopPerfTimer(perfEnabled, decorationsStart);

        double presentMs;
        try
        {
            presentMs = renderer.EndPresent();
        }
        catch (Exception ex) when (renderer.MarkUnavailable(ex.Message))
        {
            FallbackToWpfRenderer(ex.Message);
            return false;
        }

        _decorationsVisualDirty = false;

        if (_caretVisualDirty)
        {
            long caretStart = StartPerfTimer(perfEnabled);
            UpdateCaretVisual();
            caretMs = StopPerfTimer(perfEnabled, caretStart);
            _caretVisualDirty = false;
            rebuiltCaret = true;
        }

        int visibleLines = lastLine >= firstLine ? lastLine - firstLine + 1 : 0;
        int drawnLines = _renderedLastLine >= _renderedFirstLine ? _renderedLastLine - _renderedFirstLine + 1 : 0;
        var stats = new EditorRenderFrameStats(
            visibleLines,
            drawnLines,
            staticLayerRebuilds,
            DynamicLayerRedraws: 1,
            glyphRuns,
            selectionRects,
            presentMs);

        _perf.RecordGpuFrame(pixelWidth, pixelHeight, _font.Dpi, stats);
        _perf.RecordFrame(
            StopPerfTimer(perfEnabled, frameStart),
            rebuiltText,
            rebuiltGutter,
            rebuiltDecorations: true,
            rebuiltCaret,
            textMs,
            gutterMs,
            decorationMs + presentMs,
            caretMs);

        return true;
    }

    private void InitializeRequestedRenderer()
    {
        _activeRenderer = _wpfRenderer;
        _activeRenderMode = EditorRenderMode.Wpf;
        _direct2DTargetVersion = -1;

        if (_requestedRenderMode != EditorRenderMode.Direct2D)
        {
            ClearGpuVisual();
            LogRendererStateIfNeeded();
            return;
        }

        nint hwnd = (PresentationSource.FromVisual(this) as HwndSource)?.Handle ?? 0;
        if (hwnd == 0 && Window.GetWindow(this) is { } window)
            hwnd = new WindowInteropHelper(window).Handle;

        if (hwnd == 0)
        {
            FallbackToWpfRenderer("No HWND available for Direct3D interop");
            return;
        }

        var renderer = new Direct2DEditorRenderer();
        if (!renderer.TryInitialize(hwnd, _font.Dpi, out var reason))
        {
            renderer.Dispose();
            FallbackToWpfRenderer(reason ?? "Direct2D initialization failed");
            return;
        }

        _direct2DRenderer = renderer;
        _activeRenderer = renderer;
        _activeRenderMode = EditorRenderMode.Direct2D;
        _rendererFallbackReason = null;
        _wpfLayersClearedForDirect2D = false;
        MarkTextAndGutterDirty();
        LogRendererStateIfNeeded();
    }

    private void FallbackToWpfRenderer(string reason)
    {
        _rendererFallbackReason = reason;
        _activeRenderer = _wpfRenderer;
        _activeRenderMode = EditorRenderMode.Wpf;
        _direct2DRenderer?.Dispose();
        _direct2DRenderer = null;
        _wpfLayersClearedForDirect2D = false;
        ClearGpuVisual();
        MarkTextAndGutterDirty();
        _perf.LogRendererState(
            _requestedRenderMode,
            _activeRenderMode,
            Math.Max(1, (int)Math.Ceiling(Math.Max(1, ActualWidth) * Math.Max(1, _font.Dpi))),
            Math.Max(1, (int)Math.Ceiling(Math.Max(1, ActualHeight) * Math.Max(1, _font.Dpi))),
            _font.Dpi,
            reason);
    }

    private void LogRendererStateIfNeeded(int pixelWidth = -1, int pixelHeight = -1)
    {
        pixelWidth = pixelWidth > 0
            ? pixelWidth
            : Math.Max(1, (int)Math.Ceiling(Math.Max(1, ActualWidth) * Math.Max(1, _font.Dpi)));
        pixelHeight = pixelHeight > 0
            ? pixelHeight
            : Math.Max(1, (int)Math.Ceiling(Math.Max(1, ActualHeight) * Math.Max(1, _font.Dpi)));

        if (pixelWidth == _rendererLoggedPixelWidth
            && pixelHeight == _rendererLoggedPixelHeight
            && Math.Abs(_font.Dpi - _rendererLoggedDpi) < 0.001)
        {
            return;
        }

        _rendererLoggedPixelWidth = pixelWidth;
        _rendererLoggedPixelHeight = pixelHeight;
        _rendererLoggedDpi = _font.Dpi;
        _perf.LogRendererState(_requestedRenderMode, _activeRenderMode, pixelWidth, pixelHeight, _font.Dpi, _rendererFallbackReason);
    }

    private void EnsureDirect2DVisual()
    {
        if (_direct2DRenderer?.ImageSource == null) return;
        using var dc = _gpuVisual.RenderOpen();
        dc.DrawImage(_direct2DRenderer.ImageSource, new System.Windows.Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)));
    }

    private void ClearGpuVisual()
    {
        using var dc = _gpuVisual.RenderOpen();
    }

    private void ClearWpfLayerVisualsForDirect2D()
    {
        if (_wpfLayersClearedForDirect2D) return;
        using (var dc = _decorationsVisual.RenderOpen()) { }
        using (var dc = _textVisual.RenderOpen()) { }
        using (var dc = _gutterVisual.RenderOpen()) { }
        _wpfLayersClearedForDirect2D = true;
    }

    private int RebuildDirect2DTextLayer(Direct2DEditorRenderer renderer, int firstLine, int lastLine)
    {
        int glyphRuns = 0;
        renderer.ReplaceTextLayer(() => glyphRuns = RenderDirect2DTextLayer(renderer, firstLine, lastLine));
        return glyphRuns;
    }

    private int RebuildDirect2DGutterLayer(Direct2DEditorRenderer renderer, int firstLine, int lastLine)
    {
        int glyphRuns = 0;
        renderer.ReplaceGutterLayer(() => glyphRuns = RenderDirect2DGutterLayer(renderer, firstLine, lastLine));
        return glyphRuns;
    }

    private int RenderDirect2DTextLayer(Direct2DEditorRenderer renderer, int firstLine, int lastLine)
    {
        int drawFirst = Math.Max(0, firstLine - RenderBufferLines);
        int drawLast = Math.Min(_buffer.Count - 1, lastLine + RenderBufferLines);
        if (drawLast < drawFirst) return 0;

        int glyphRuns = 0;
        _textYBias = GetLineTopY(drawFirst);

        bool hasLongLine = false;
        for (int i = drawFirst; i <= drawLast; i++)
            if (_buffer[i].Length > LongLineThreshold) { hasLongLine = true; break; }
        _textXBias = hasLongLine ? _offset.X : 0;

        renderer.SetTextFormat(_font.FontFamilyName, _font.FontSize, _font.LineHeight, _font.GlyphBaseline, _font.CharWidth * TabSize, _font.EditorFontWeight);

        if (_indentGuides)
            RenderDirect2DIndentGuides(renderer, drawFirst, drawLast);

        if (UseSequentialSyntaxStates)
            EnsureLineStates(drawLast);

        for (int i = drawFirst; i <= drawLast; i++)
        {
            if (IsLineHidden(i)) continue;
            var line = _buffer[i];
            if (line.Length == 0) continue;
            double x = _gutterWidth + GutterPadding;

            var inState = GetSyntaxInputState(i);
            if (!_tokenCache.TryGetValue(i, out var cached)
                || cached.content != line || cached.inState != inState)
            {
                var tokens = line.Length > LongLineThreshold
                    ? []
                    : SyntaxManager.Tokenize(line, _grammar, inState, out _);
                _tokenCache[i] = (line, inState, tokens);
                cached = _tokenCache[i];
            }

            if (!IsWordWrapActive || VisualLineCount(i) <= 1)
            {
                double y = GetLineTopY(i) - _textYBias;
                int segStart = 0;
                int segEnd = line.Length;
                if (line.Length > LongLineThreshold)
                {
                    segStart = Math.Max(0, (int)(_offset.X / _font.CharWidth) - 2);
                    segEnd = Math.Min(line.Length,
                        (int)((_offset.X + _viewport.Width) / _font.CharWidth) + 2);
                }

                glyphRuns += RenderDirect2DLineTokens(renderer, line, x + segStart * _font.CharWidth - _textXBias,
                    y, segStart, segEnd, cached.tokens);
            }
            else
            {
                int vCount = VisualLineCount(i);
                int baseCumul = _wrap.CumulOffset(i);
                int visFirst = Math.Max(0, (int)(_offset.Y / _font.LineHeight) - RenderBufferLines - baseCumul);
                int visLast = Math.Min(vCount - 1,
                    (int)((_offset.Y + _viewport.Height) / _font.LineHeight) + RenderBufferLines - baseCumul);
                for (int w = Math.Max(0, visFirst); w <= visLast; w++)
                {
                    int segStart = WrapColStart(i, w);
                    int segEnd = w + 1 < vCount ? WrapColStart(i, w + 1) : line.Length;
                    double y = (baseCumul + w) * _font.LineHeight - _textYBias;
                    double wx = x + WrapIndentPx(i, w);
                    glyphRuns += RenderDirect2DLineTokens(renderer, line, wx, y, segStart, segEnd, cached.tokens);
                }
            }
        }

        _renderedFirstLine = drawFirst;
        _renderedLastLine = drawLast;
        if (hasLongLine)
        {
            _renderedScrollX = _offset.X;
            _renderedScrollY = _offset.Y;
        }
        else
        {
            _renderedScrollX = double.NaN;
        }

        if (_tokenCache.Count > (drawLast - drawFirst + 1) * 3)
        {
            int margin = drawLast - drawFirst + 1;
            int pruneBelow = drawFirst - margin;
            int pruneAbove = drawLast + margin;
            _pruneKeys.Clear();
            foreach (var key in _tokenCache.Keys)
                if (key < pruneBelow || key > pruneAbove)
                    _pruneKeys.Add(key);
            foreach (var key in _pruneKeys)
                _tokenCache.Remove(key);
        }

        return glyphRuns;
    }

    private int RenderDirect2DLineTokens(Direct2DEditorRenderer renderer, string line, double x, double y,
        int segStart, int segEnd, List<SyntaxToken> tokens)
    {
        if (segEnd <= segStart) return 0;
        int glyphRuns = 0;
        if (tokens.Count == 0)
        {
            return renderer.DrawText(line, segStart, segEnd - segStart, x, y, _font.CharWidth, _font.LineHeight, ThemeManager.EditorFg);
        }

        var editorBrush = ThemeManager.EditorFg;
        int pos = segStart;
        int runStart = segStart;
        int runEnd = segStart;
        Brush? runBrush = null;

        void FlushRun()
        {
            if (runBrush == null || runEnd <= runStart) return;
            glyphRuns += renderer.DrawText(line, runStart, runEnd - runStart,
                x + (runStart - segStart) * _font.CharWidth, y, _font.CharWidth, _font.LineHeight, runBrush);
        }

        void AppendRun(int start, int end, Brush brush)
        {
            if (end <= start) return;
            if (runBrush != null && ReferenceEquals(runBrush, brush) && start == runEnd)
            {
                runEnd = end;
                return;
            }

            FlushRun();
            runBrush = brush;
            runStart = start;
            runEnd = end;
        }

        foreach (var token in tokens)
        {
            int tEnd = token.Start + token.Length;
            if (tEnd <= segStart) continue;
            if (token.Start >= segEnd) break;
            int drawStart = Math.Max(token.Start, segStart);
            int drawEnd = Math.Min(tEnd, segEnd);

            if (drawStart > pos)
                AppendRun(pos, drawStart, editorBrush);
            var brush = ThemeManager.GetScopeBrush(token.Scope);
            AppendRun(drawStart, drawEnd, brush);
            pos = drawEnd;
        }
        if (pos < segEnd)
            AppendRun(pos, segEnd, editorBrush);

        FlushRun();
        return glyphRuns;
    }

    private void RenderDirect2DIndentGuides(Direct2DEditorRenderer renderer, int drawFirst, int drawLast)
    {
        if (_buffer.Count == 0) return;
        for (int i = drawFirst; i <= drawLast; i++)
            if (_buffer[i].Length > LongLineThreshold) return;

        double baseX = _gutterWidth + GutterPadding;
        var brush = ThemeManager.IndentGuidePen.Brush;
        float thickness = (float)Math.Max(1, ThemeManager.IndentGuidePen.Thickness);

        for (int i = drawFirst; i <= drawLast; i++)
        {
            var block = GetStructuralBlockInfo(i);
            if (block == null) continue;
            DrawDirectIndentGuide(renderer, baseX, brush, thickness, i, block.Value.CloseLine);
        }

        int searchLine = drawFirst;
        int searchCol = 0;
        while (true)
        {
            int? openerLine = BracketMatcher.FindEnclosingOpenBrace(_buffer, searchLine, searchCol, LiteralSkip);
            if (openerLine == null) break;
            int i = openerLine.Value;

            var block = GetStructuralBlockInfo(i);
            if (block == null) { searchLine = i; searchCol = 0; continue; }
            int closeLine = block.Value.CloseLine;

            searchLine = i;
            searchCol = 0;

            if (closeLine < drawFirst) continue;
            DrawDirectIndentGuide(renderer, baseX, brush, thickness, i, closeLine);
        }
    }

    private void DrawDirectIndentGuide(Direct2DEditorRenderer renderer, double baseX, Brush brush, float thickness, int openLine, int closeLine)
    {
        int indentCol = MeasureIndentColumns(_buffer[closeLine], TabSize);
        if (indentCol == 0) return;

        int guideFirst = openLine + 1;
        int guideLast = closeLine - 1;
        if (guideLast < guideFirst) return;

        double x = baseX + indentCol * _font.CharWidth;
        double yTop, yBot;
        if (!IsWordWrapActive && !HasFoldLayout)
        {
            yTop = guideFirst * _font.LineHeight;
            yBot = (guideLast + 1) * _font.LineHeight;
        }
        else
        {
            yTop = _wrap.CumulOffset(guideFirst) * _font.LineHeight;
            yBot = (_wrap.CumulOffset(guideLast) + VisualLineCount(guideLast)) * _font.LineHeight;
        }

        yTop -= _textYBias;
        yBot -= _textYBias;
        if (yTop < yBot)
            renderer.DrawLine(x, yTop, x, yBot, brush, thickness);
    }

    private int RenderDirect2DGutterLayer(Direct2DEditorRenderer renderer, int firstLine, int lastLine)
    {
        int drawFirst = Math.Max(0, firstLine - RenderBufferLines);
        int drawLast = Math.Min(_buffer.Count - 1, lastLine + RenderBufferLines);
        if (drawLast < drawFirst) return 0;

        int glyphRuns = 0;
        _gutterYBias = GetLineTopY(drawFirst);
        renderer.SetTextFormat(_font.FontFamilyName, _font.FontSize, _font.LineHeight, _font.GlyphBaseline, _font.CharWidth * TabSize, _font.EditorFontWeight);

        double bgTop, bgBottom;
        if (IsWordWrapActive || HasFoldLayout)
        {
            bgTop = _wrap.CumulOffset(drawFirst) * _font.LineHeight;
            int lastVisual = _wrap.CumulOffset(drawLast) + VisualLineCount(drawLast);
            bgBottom = lastVisual * _font.LineHeight;
        }
        else
        {
            bgTop = drawFirst * _font.LineHeight;
            bgBottom = (drawLast + 1) * _font.LineHeight;
        }
        bgTop -= _gutterYBias;
        bgBottom -= _gutterYBias;

        renderer.FillRectangle(0, bgTop, _gutterWidth, bgBottom - bgTop, ThemeManager.EditorBg);
        renderer.DrawLine(_gutterWidth, bgTop, _gutterWidth, bgBottom, _gutterSepPen.Brush, (float)_gutterSepPen.Thickness);

        double foldCenterX = _gutterWidth - FoldGutterWidth / 2 - 2;
        for (int i = drawFirst; i <= drawLast; i++)
        {
            if (IsLineHidden(i)) continue;
            double y = GetLineTopY(i) - _gutterYBias;
            var brush = i == _caretLine
                ? ThemeManager.ActiveLineNumberFg : ThemeManager.GutterFg;
            int lineNum = i + 1;
            if (!_lineNumStrings.TryGetValue(lineNum, out var numStr))
            {
                numStr = lineNum.ToString();
                _lineNumStrings[lineNum] = numStr;
            }
            double numWidth = numStr.Length * _font.CharWidth;
            glyphRuns += renderer.DrawText(numStr, 0, numStr.Length,
                _gutterWidth - FoldGutterWidth - numWidth - GutterPadding, y, _font.CharWidth, _font.LineHeight, brush);

            if (IsStructuralBlockOpen(i))
            {
                bool isFolded = _foldedLines.Contains(i);
                bool isHovered = i == _hoverFoldLine;
                double cy = y + _font.LineHeight / 2;
                double sz = Math.Min(8, _font.LineHeight * 0.45);
                double btnSize = _font.LineHeight * 0.85;
                double btnX = foldCenterX - btnSize / 2;
                double btnY = y + (_font.LineHeight - btnSize) / 2;

                if (isHovered)
                    renderer.FillRoundedRectangle(btnX, btnY, btnSize, btnSize, 3, ThemeManager.FoldHoverBrush);

                var fg = isHovered ? ThemeManager.EditorFg : ThemeManager.GutterFg;
                if (isFolded)
                {
                    renderer.FillTriangle(
                        foldCenterX - sz * 0.4, cy - sz * 0.55,
                        foldCenterX + sz * 0.5, cy,
                        foldCenterX - sz * 0.4, cy + sz * 0.55,
                        fg);
                }
                else
                {
                    renderer.FillTriangle(
                        foldCenterX - sz * 0.55, cy - sz * 0.35,
                        foldCenterX + sz * 0.55, cy - sz * 0.35,
                        foldCenterX, cy + sz * 0.45,
                        fg);
                }
            }
        }

        _gutterRenderedFirstLine = drawFirst;
        _gutterRenderedLastLine = drawLast;
        return glyphRuns;
    }

    private int RenderDirect2DDecorations(Direct2DEditorRenderer renderer, int firstLine, int lastLine)
    {
        int selectionRects = 0;

        if (_caretLine >= firstLine && _caretLine <= lastLine)
        {
            double curLineY = GetVisualY(_caretLine, _caretCol) - _offset.Y;
            renderer.FillRectangle(0, curLineY, ActualWidth, _font.LineHeight, ThemeManager.CurrentLineBrush);
        }

        if (_selection.HasSelection)
        {
            var (sl, sc, el, ec) = _selection.GetOrdered(_caretLine, _caretCol);
            for (int i = Math.Max(firstLine, sl); i <= Math.Min(lastLine, el); i++)
            {
                if (IsLineHidden(i)) continue;
                int selStart = i == sl ? sc : 0;
                int selEnd = i == el ? ec : _buffer[i].Length;

                if (!IsWordWrapActive && !HasFoldLayout)
                {
                    double y = i * _font.LineHeight - _offset.Y;
                    double x1 = _gutterWidth + GutterPadding + selStart * _font.CharWidth - _offset.X;
                    double x2 = _gutterWidth + GutterPadding + selEnd * _font.CharWidth - _offset.X;
                    if (i > sl && i < el) x2 = Math.Max(x2, ActualWidth);
                    if (i == sl && i != el) x2 = Math.Max(x2, ActualWidth);
                    double drawX = Math.Max(x1, _gutterWidth + GutterPadding);
                    double drawW = Math.Max(0, x2 - drawX);
                    if (drawW > 0)
                    {
                        renderer.FillRectangle(drawX, y, drawW, _font.LineHeight, ThemeManager.SelectionBrush);
                        selectionRects++;
                    }
                }
                else
                {
                    selectionRects += RenderDirect2DWrappedSelection(renderer, i, selStart, selEnd, sl, sc, el);
                }
            }
        }

        var visibleFindMatches = _find.GetMatchesInRange(firstLine, lastLine);
        if (visibleFindMatches.Count > 0)
        {
            var currentMatch = _find.GetCurrentMatch();
            foreach (var match in visibleFindMatches)
            {
                var (mLine, mCol, mLen) = match;
                if (IsLineHidden(mLine)) continue;
                var brush = currentMatch == match
                    ? ThemeManager.FindMatchCurrentBrush
                    : ThemeManager.FindMatchBrush;

                if (!IsWordWrapActive && !HasFoldLayout)
                {
                    double pxStart = mCol * _font.CharWidth;
                    double pxEnd = (mCol + mLen) * _font.CharWidth;
                    double mx = _gutterWidth + GutterPadding + pxStart - _offset.X;
                    double my = mLine * _font.LineHeight - _offset.Y;
                    double textLeft = _gutterWidth + GutterPadding;
                    double clippedX = Math.Max(mx, textLeft);
                    double clippedW = Math.Max(0, mx + (pxEnd - pxStart) - clippedX);
                    if (clippedW > 0)
                        renderer.FillRectangle(clippedX, my, clippedW, _font.LineHeight, brush);
                }
                else
                {
                    int col = mCol;
                    int remaining = mLen;
                    int vCount = VisualLineCount(mLine);
                    while (remaining > 0)
                    {
                        int visLine = LogicalToVisualLine(mLine, col);
                        int wrapIndex = visLine - _wrap.CumulOffset(mLine);
                        int wrapStart = WrapColStart(mLine, wrapIndex);
                        int colInWrap = col - wrapStart;
                        int wrapEnd = wrapIndex + 1 < vCount ? WrapColStart(mLine, wrapIndex + 1) : _buffer[mLine].Length;
                        int charsOnThisLine = Math.Min(remaining, wrapEnd - col);
                        double indentPx = WrapIndentPx(mLine, wrapIndex);
                        double mx = _gutterWidth + GutterPadding + indentPx + colInWrap * _font.CharWidth;
                        double my = visLine * _font.LineHeight - _offset.Y;
                        renderer.FillRectangle(mx, my, charsOnThisLine * _font.CharWidth, _font.LineHeight, brush);
                        col += charsOnThisLine;
                        remaining -= charsOnThisLine;
                    }
                }
            }
        }

        if (IsKeyboardFocused && !_selection.HasSelection)
        {
            if (_bracketMatchDirty)
            {
                _bracketMatchCache = _caretLine < _buffer.Count && _buffer[_caretLine].Length > LongLineThreshold
                    ? null
                    : BracketMatcher.FindMatch(_buffer, _caretLine, _caretCol, LiteralSkip);
                _bracketMatchDirty = false;
            }
            if (_bracketMatchCache is var (bl, bc, ml, mc))
            {
                double bracketTextLeft = _gutterWidth + GutterPadding;
                if (bl >= firstLine && bl <= lastLine && !IsLineHidden(bl))
                {
                    var (bx, by) = GetPixelForPosition(bl, bc);
                    double cbx = Math.Max(bx, bracketTextLeft);
                    double cbw = Math.Max(0, bx + _font.CharWidth - cbx);
                    if (cbw > 0)
                        renderer.FillAndStrokeRectangle(cbx, by, cbw, _font.LineHeight,
                            ThemeManager.MatchingBracketBrush, ThemeManager.MatchingBracketPen);
                }
                if (ml >= firstLine && ml <= lastLine && !IsLineHidden(ml))
                {
                    var (mx, my) = GetPixelForPosition(ml, mc);
                    double cmx = Math.Max(mx, bracketTextLeft);
                    double cmw = Math.Max(0, mx + _font.CharWidth - cmx);
                    if (cmw > 0)
                        renderer.FillAndStrokeRectangle(cmx, my, cmw, _font.LineHeight,
                            ThemeManager.MatchingBracketBrush, ThemeManager.MatchingBracketPen);
                }
            }
        }

        return selectionRects;
    }

    private int RenderDirect2DWrappedSelection(Direct2DEditorRenderer renderer, int line, int selStart, int selEnd, int sl, int sc, int el)
    {
        int rects = 0;
        int vCount = VisualLineCount(line);
        int baseCumul = _wrap.CumulOffset(line);
        int visFirst = Math.Max(0, (int)(_offset.Y / _font.LineHeight) - RenderBufferLines - baseCumul);
        int visLast = Math.Min(vCount - 1,
            (int)((_offset.Y + _viewport.Height) / _font.LineHeight) + RenderBufferLines - baseCumul);
        for (int w = Math.Max(0, visFirst); w <= visLast; w++)
        {
            int wStart = WrapColStart(line, w);
            int wEnd = w + 1 < vCount ? WrapColStart(line, w + 1) : _buffer[line].Length;
            int sA = Math.Max(selStart, wStart);
            int sB = Math.Min(selEnd, wEnd);
            if (sA >= sB && !(line != el && w == vCount - 1 && selEnd >= wEnd)) continue;

            double indentPx = WrapIndentPx(line, w);
            double y = (_wrap.CumulOffset(line) + w) * _font.LineHeight - _offset.Y;
            double x1 = _gutterWidth + GutterPadding + indentPx + (sA - wStart) * _font.CharWidth;
            double x2 = sB > sA
                ? _gutterWidth + GutterPadding + indentPx + (sB - wStart) * _font.CharWidth
                : x1;

            bool extendToEdge = (line > sl || sA > selStart || w > 0) && (line < el || sB < selEnd || w < vCount - 1);
            if (line != el && w == vCount - 1) extendToEdge = true;
            if (line == sl && line != el && w >= (LogicalToVisualLine(line, sc) - _wrap.CumulOffset(line))) extendToEdge = true;
            if (extendToEdge && line != el) x2 = Math.Max(x2, ActualWidth);
            if (line == sl && line != el && sB >= wEnd) x2 = Math.Max(x2, ActualWidth);

            double drawX = Math.Max(x1, _gutterWidth + GutterPadding);
            double drawW = Math.Max(0, x2 - drawX);
            if (drawW > 0)
            {
                renderer.FillRectangle(drawX, y, drawW, _font.LineHeight, ThemeManager.SelectionBrush);
                rects++;
            }
        }

        return rects;
    }

    private sealed class Direct2DEditorRenderer : IEditorRenderer
    {
        private readonly D3DImage _image = new();
        private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
        private ID3D11Device? _d3d11Device;
        private ID3D11DeviceContext? _d3d11Context;
        private IDirect3D9Ex? _d3d9;
        private IDirect3DDevice9Ex? _d3d9Device;
        private ID2D1Factory1? _d2dFactory;
        private ID2D1Device? _d2dDevice;
        private ID2D1DeviceContext? _d2dContext;
        private IDWriteFactory? _dwriteFactory;
        private IDWriteRenderingParams? _textRenderingParams;
        private IDWriteTextFormat? _textFormat;
        private ID3D11Texture2D? _texture;
        private IDXGISurface? _dxgiSurface;
        private ID2D1Bitmap1? _targetBitmap;
        private IDirect3DTexture9? _d3d9Texture;
        private IDirect3DSurface9? _d3d9Surface;
        private ID2D1CommandList? _textLayer;
        private ID2D1CommandList? _gutterLayer;
        private string _fontFormatKey = "";
        private int _pixelWidth;
        private int _pixelHeight;
        private double _dpi;
        private Vortice.Direct2D1.TextAntialiasMode _textAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        public EditorRenderMode Mode => EditorRenderMode.Direct2D;
        public bool IsAvailable { get; private set; }
        public string? FallbackReason { get; private set; }
        public ImageSource ImageSource => _image;
        public int TargetVersion { get; private set; }

        public bool TryInitialize(nint hwnd, double dpi, out string? reason)
        {
            try
            {
                _d3d11Device = D3D11CreateDevice(
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    [
                        Vortice.Direct3D.FeatureLevel.Level_11_1,
                        Vortice.Direct3D.FeatureLevel.Level_11_0,
                        Vortice.Direct3D.FeatureLevel.Level_10_1,
                        Vortice.Direct3D.FeatureLevel.Level_10_0,
                        Vortice.Direct3D.FeatureLevel.Level_9_3
                    ]);
                _d3d11Context = _d3d11Device.ImmediateContext;
                using var dxgiDevice = _d3d11Device.QueryInterface<IDXGIDevice>();

                _d2dFactory = D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded, DebugLevel.None);
                _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
                _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

                _dwriteFactory = DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);
                using (var defaultTextRenderingParams = _dwriteFactory.CreateRenderingParams())
                {
                    _textRenderingParams = _dwriteFactory.CreateCustomRenderingParams(
                        defaultTextRenderingParams.Gamma,
                        defaultTextRenderingParams.EnhancedContrast,
                        0.0f,
                        PixelGeometry.Flat,
                        RenderingMode.NaturalSymmetric);
                }
                ApplyTextRenderingState();

                _d3d9 = Direct3DCreate9Ex();
                var present = new Vortice.Direct3D9.PresentParameters
                {
                    Windowed = true,
                    SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
                    DeviceWindowHandle = hwnd,
                    BackBufferFormat = D3D9Format.Unknown,
                    PresentationInterval = PresentInterval.Immediate
                };
                _d3d9Device = _d3d9.CreateDeviceEx(
                    0,
                    DeviceType.Hardware,
                    hwnd,
                    CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                    present);

                _dpi = dpi;
                IsAvailable = true;
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                FallbackReason = reason;
                IsAvailable = false;
                Dispose();
                return false;
            }
        }

        public bool EnsureTarget(int pixelWidth, int pixelHeight, double dpi, out string? reason)
        {
            reason = null;
            if (!IsAvailable || _d2dContext == null || _d3d11Device == null || _d3d9Device == null)
            {
                reason = FallbackReason ?? "Direct2D renderer is unavailable";
                return false;
            }

            pixelWidth = Math.Max(1, pixelWidth);
            pixelHeight = Math.Max(1, pixelHeight);
            if (_targetBitmap != null
                && pixelWidth == _pixelWidth
                && pixelHeight == _pixelHeight
                && Math.Abs(dpi - _dpi) < 0.001)
            {
                return true;
            }

            try
            {
                DisposeTargetResources();
                _pixelWidth = pixelWidth;
                _pixelHeight = pixelHeight;
                _dpi = dpi;

                var textureDesc = new Texture2DDescription(
                    D3D11Format.B8G8R8A8_UNorm,
                    (uint)pixelWidth,
                    (uint)pixelHeight,
                    1,
                    1,
                    BindFlags.RenderTarget | BindFlags.ShaderResource,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    1,
                    0,
                    ResourceOptionFlags.Shared);

                _texture = _d3d11Device.CreateTexture2D(textureDesc);
                _dxgiSurface = _texture.QueryInterface<IDXGISurface>();
                var bitmapProps = new BitmapProperties1(
                    new DCommonPixelFormat(D3D11Format.B8G8R8A8_UNorm, DCommonAlphaMode.Ignore),
                    96,
                    96,
                    D2DBitmapOptions.Target | D2DBitmapOptions.CannotDraw);
                _targetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(_dxgiSurface, bitmapProps);

                using var sharedResource = _texture.QueryInterface<IDXGIResource>();
                nint sharedHandle = sharedResource.SharedHandle;
                _d3d9Texture = _d3d9Device.CreateTexture(
                    (uint)pixelWidth,
                    (uint)pixelHeight,
                    1,
                    D3D9Usage.RenderTarget,
                    D3D9Format.A8R8G8B8,
                    Pool.Default,
                    ref sharedHandle);
                _d3d9Surface = _d3d9Texture.GetSurfaceLevel(0);

                _image.Lock();
                _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer);
                _image.Unlock();
                TargetVersion++;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                FallbackReason = reason;
                IsAvailable = false;
                return false;
            }
        }

        public void SetTextFormat(string familyName, double fontSize, double lineHeight, double baseline, double tabStop, string weightName)
        {
            if (_dwriteFactory == null) return;
            string key = $"{familyName}|{fontSize:F3}|{lineHeight:F3}|{baseline:F3}|{tabStop:F3}|{weightName}";
            if (key == _fontFormatKey && _textFormat != null) return;

            _textFormat?.Dispose();
            _textFormat = _dwriteFactory.CreateTextFormat(
                familyName,
                MapFontWeight(weightName),
                Vortice.DirectWrite.FontStyle.Normal,
                Vortice.DirectWrite.FontStretch.Normal,
                (float)Scale(fontSize));
            _textFormat.WordWrapping = WordWrapping.NoWrap;
            _textFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Leading;
            _textFormat.ParagraphAlignment = ParagraphAlignment.Near;
            _textFormat.IncrementalTabStop = (float)Math.Max(1, Scale(tabStop));
            _textFormat.SetLineSpacing(LineSpacingMethod.Uniform, (float)Math.Max(1, Scale(lineHeight)), (float)Math.Max(0, Scale(baseline)));
            _fontFormatKey = key;
        }

        public void ReplaceTextLayer(Action render)
        {
            _textLayer?.Dispose();
            _textLayer = BuildCommandList(render);
        }

        public void ReplaceGutterLayer(Action render)
        {
            _gutterLayer?.Dispose();
            _gutterLayer = BuildCommandList(render);
        }

        private ID2D1CommandList BuildCommandList(Action render)
        {
            if (_d2dContext == null) throw new InvalidOperationException("Direct2D context is unavailable.");
            var previousTarget = _d2dContext.Target;
            var layer = _d2dContext.CreateCommandList();
            _d2dContext.Target = layer;
            _d2dContext.Transform = Matrix3x2.Identity;
            ApplyTextRenderingState();
            _d2dContext.BeginDraw();
            render();
            _d2dContext.EndDraw().CheckError();
            layer.Close().CheckError();
            _d2dContext.Target = previousTarget;
            return layer;
        }

        public void BeginPresent(Brush background)
        {
            if (_d2dContext == null || _targetBitmap == null)
                throw new InvalidOperationException("Direct2D target is unavailable.");
            _d2dContext.Target = _targetBitmap;
            _d2dContext.Transform = Matrix3x2.Identity;
            ApplyTextRenderingState();
            _d2dContext.BeginDraw();
            _d2dContext.Clear(ToColor4(background));
        }

        public void DrawStaticLayers(Matrix3x2 textTransform, RawRectF textClip, Matrix3x2 gutterTransform)
        {
            if (_d2dContext == null) return;

            if (_textLayer != null)
            {
                _d2dContext.Transform = Matrix3x2.Identity;
                _d2dContext.PushAxisAlignedClip(Scale(textClip), AntialiasMode.Aliased);
                _d2dContext.Transform = Scale(textTransform);
                _d2dContext.DrawImage(_textLayer, Vortice.Direct2D1.InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
                _d2dContext.Transform = Matrix3x2.Identity;
                _d2dContext.PopAxisAlignedClip();
            }

            if (_gutterLayer != null)
            {
                _d2dContext.Transform = Scale(gutterTransform);
                _d2dContext.DrawImage(_gutterLayer, Vortice.Direct2D1.InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
                _d2dContext.Transform = Matrix3x2.Identity;
            }
        }

        public double EndPresent()
        {
            if (_d2dContext == null || _d3d11Context == null)
                throw new InvalidOperationException("Direct2D target is unavailable.");

            long start = Stopwatch.GetTimestamp();
            _d2dContext.EndDraw().CheckError();
            _d3d11Context.Flush();

            if (_image.IsFrontBufferAvailable)
            {
                _image.Lock();
                _image.AddDirtyRect(new Int32Rect(0, 0, _pixelWidth, _pixelHeight));
                _image.Unlock();
            }

            return EditorPerformanceTrace.ElapsedMilliseconds(start);
        }

        public int DrawText(string text, int startIndex, int length, double x, double y, double charWidth, double lineHeight, Brush brush)
        {
            if (_d2dContext == null || _textFormat == null || length <= 0) return 0;
            var run = text.Substring(startIndex, length);
            var rect = new D2DRect(
                (float)Scale(x),
                (float)Scale(y),
                (float)Math.Max(Scale(charWidth), Scale(length * charWidth + charWidth)),
                (float)Math.Max(1, Scale(lineHeight)));
            _d2dContext.DrawText(run, _textFormat, rect, BrushFor(brush), DrawTextOptions.Clip, MeasuringMode.Natural);
            return 1;
        }

        public void FillRectangle(double x, double y, double width, double height, Brush brush)
        {
            if (_d2dContext == null || width <= 0 || height <= 0) return;
            var rect = RectFrom(Scale(x), Scale(y), Scale(width), Scale(height));
            _d2dContext.FillRectangle(rect, BrushFor(brush));
        }

        public void DrawRectangle(double x, double y, double width, double height, Brush brush, float thickness)
        {
            if (_d2dContext == null || width <= 0 || height <= 0) return;
            var rect = RectFrom(Scale(x), Scale(y), Scale(width), Scale(height));
            _d2dContext.DrawRectangle(rect, BrushFor(brush), Math.Max(0.5f, (float)Scale(thickness)));
        }

        public void FillAndStrokeRectangle(double x, double y, double width, double height, Brush fill, Pen stroke)
        {
            FillRectangle(x, y, width, height, fill);
            DrawRectangle(x, y, width, height, stroke.Brush, (float)stroke.Thickness);
        }

        public void DrawLine(double x1, double y1, double x2, double y2, Brush brush, float thickness)
        {
            if (_d2dContext == null) return;
            _d2dContext.DrawLine(
                new Vector2((float)Scale(x1), (float)Scale(y1)),
                new Vector2((float)Scale(x2), (float)Scale(y2)),
                BrushFor(brush),
                Math.Max(0.5f, (float)Scale(thickness)),
                null);
        }

        public void FillRoundedRectangle(double x, double y, double width, double height, double radius, Brush brush)
        {
            if (_d2dContext == null || width <= 0 || height <= 0) return;
            var rounded = new RoundedRectangle(
                new System.Drawing.RectangleF((float)Scale(x), (float)Scale(y), (float)Scale(width), (float)Scale(height)),
                (float)Scale(radius),
                (float)Scale(radius));
            _d2dContext.FillRoundedRectangle(rounded, BrushFor(brush));
        }

        public void FillTriangle(double x1, double y1, double x2, double y2, double x3, double y3, Brush brush)
        {
            if (_d2dContext == null || _d2dFactory == null) return;
            using var geometry = _d2dFactory.CreatePathGeometry();
            using var sink = geometry.Open();
            sink.BeginFigure(new Vector2((float)Scale(x1), (float)Scale(y1)), FigureBegin.Filled);
            sink.AddLine(new Vector2((float)Scale(x2), (float)Scale(y2)));
            sink.AddLine(new Vector2((float)Scale(x3), (float)Scale(y3)));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
            _d2dContext.FillGeometry(geometry, BrushFor(brush));
        }

        public bool MarkUnavailable(string reason)
        {
            FallbackReason = reason;
            IsAvailable = false;
            return true;
        }

        private ID2D1SolidColorBrush BrushFor(Brush brush)
        {
            if (_d2dContext == null) throw new InvalidOperationException("Direct2D context is unavailable.");
            uint key = ColorKey(brush);
            if (_brushes.TryGetValue(key, out var cached)) return cached;
            var created = _d2dContext.CreateSolidColorBrush(ToColor4(key));
            _brushes[key] = created;
            return created;
        }

        private void DisposeTargetResources()
        {
            if (_d2dContext != null)
                _d2dContext.Target = null!;
            _textLayer?.Dispose();
            _gutterLayer?.Dispose();
            _textLayer = null;
            _gutterLayer = null;
            _targetBitmap?.Dispose();
            _dxgiSurface?.Dispose();
            _texture?.Dispose();
            _d3d9Surface?.Dispose();
            _d3d9Texture?.Dispose();
            _targetBitmap = null;
            _dxgiSurface = null;
            _texture = null;
            _d3d9Surface = null;
            _d3d9Texture = null;
        }

        public void Dispose()
        {
            DisposeTargetResources();
            foreach (var brush in _brushes.Values)
                brush.Dispose();
            _brushes.Clear();
            _textFormat?.Dispose();
            _textRenderingParams?.Dispose();
            _dwriteFactory?.Dispose();
            _d2dContext?.Dispose();
            _d2dDevice?.Dispose();
            _d2dFactory?.Dispose();
            _d3d9Device?.Dispose();
            _d3d9?.Dispose();
            _d3d11Context?.Dispose();
            _d3d11Device?.Dispose();
        }

        private static RawRectF RectFrom(double x, double y, double width, double height) =>
            new((float)x, (float)y, (float)(x + width), (float)(y + height));

        private double Scale(double value) => value * _dpi;

        private RawRectF Scale(RawRectF rect) =>
            new(
                (float)Scale(rect.Left),
                (float)Scale(rect.Top),
                (float)Scale(rect.Right),
                (float)Scale(rect.Bottom));

        private Matrix3x2 Scale(Matrix3x2 matrix)
        {
            matrix.M31 = MathF.Round((float)Scale(matrix.M31));
            matrix.M32 = MathF.Round((float)Scale(matrix.M32));
            return matrix;
        }

        private void ApplyTextRenderingState()
        {
            if (_d2dContext == null) return;
            _d2dContext.TextAntialiasMode = _textAntialiasMode;
            if (_textRenderingParams != null)
                _d2dContext.TextRenderingParams = _textRenderingParams;
        }

        private static Vortice.DirectWrite.FontWeight MapFontWeight(string weightName)
        {
            var normalized = weightName.Replace(" ", "", StringComparison.Ordinal);
            return Enum.TryParse<Vortice.DirectWrite.FontWeight>(normalized, ignoreCase: true, out var weight)
                ? weight
                : Vortice.DirectWrite.FontWeight.Normal;
        }

        private static uint ColorKey(Brush brush)
        {
            if (brush is SolidColorBrush solid)
            {
                var color = solid.Color;
                double opacity = Math.Clamp(solid.Opacity * brush.Opacity, 0, 1);
                byte alpha = (byte)Math.Clamp((int)Math.Round(color.A * opacity), 0, 255);
                return ((uint)alpha << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            }
            return 0xFFFF00FF;
        }

        private static Color4 ToColor4(Brush brush) => ToColor4(ColorKey(brush));

        private static Color4 ToColor4(uint argb)
        {
            float a = ((argb >> 24) & 0xFF) / 255f;
            float r = ((argb >> 16) & 0xFF) / 255f;
            float g = ((argb >> 8) & 0xFF) / 255f;
            float b = (argb & 0xFF) / 255f;
            return new Color4(r, g, b, a);
        }
    }
}
