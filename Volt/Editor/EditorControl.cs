using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Volt;

public class EditorControl : FrameworkElement, IScrollInfo
{
    // ── Extracted components ─────────────────────────────────────────
    private readonly TextBuffer _buffer = new();
    private readonly UndoManager _undoManager = new();
    private int _cleanUndoDepth; // undo stack depth when last marked clean (-1 = unreachable)
    private readonly SelectionManager _selection = new();
    private readonly FindManager _find = new();
    private readonly FontManager _font = new();

    // ── Managers (injected via constructor) ──────────────────────────
    public ThemeManager ThemeManager { get; }
    public LanguageManager LanguageManager { get; }

    private ILanguageService? _languageService;
    private LanguageSnapshot? _languageSnapshot;
    private long _languageSnapshotGeneration = -1;

    public string LanguageName => _languageService?.Name ?? "Plain Text";

    public void SetLanguage(ILanguageService? languageService)
    {
        _languageService = languageService;
        InvalidateLanguageAnalysis();
    }

    // ── Caret ────────────────────────────────────────────────────────
    private int _caretLine;
    private int _caretCol;
    private int _preferredCol = -1; // sticky column for vertical movement
    private int _prevCaretLine = -1;

    // ── Settings ───────────────────────────────────────────────────────
    public int TabSize { get; set; } = 4;
    public bool BlockCaret { get; set; }
    private bool _wordWrapAtWords = true;
    public bool WordWrapAtWords
    {
        get => _wordWrapAtWords;
        set
        {
            if (_wordWrapAtWords == value) return;
            _wordWrapAtWords = value;
            if (_wordWrap) InvalidateWrapLayout();
        }
    }

    private bool _wordWrapIndent = true;
    public bool WordWrapIndent
    {
        get => _wordWrapIndent;
        set
        {
            if (_wordWrapIndent == value) return;
            _wordWrapIndent = value;
            if (_wordWrap) InvalidateWrapLayout();
        }
    }

    private bool _wordWrap;
    public bool WordWrap
    {
        get => _wordWrap;
        set
        {
            if (value && ShouldSuppressWordWrap())
                value = false;
            if (_wordWrap == value) return;
            // Anchor to top visible line so toggling wrap doesn't shift the viewport
            int anchorLine;
            int anchorWrap = 0;
            double anchorDelta;
            if (_wordWrap && _wrap.HasValidData(_buffer.Count) && _wrap.TotalVisualLines > 0)
            {
                int topVisual = Math.Clamp((int)(_offset.Y / _font.LineHeight), 0, _wrap.TotalVisualLines - 1);
                (anchorLine, anchorWrap) = VisualToLogical(topVisual);
                anchorDelta = _offset.Y - (_wrap.CumulOffset(anchorLine) + anchorWrap) * _font.LineHeight;
            }
            else
            {
                anchorLine = Math.Clamp((int)(_offset.Y / _font.LineHeight), 0, Math.Max(0, _buffer.Count - 1));
                anchorDelta = _offset.Y - anchorLine * _font.LineHeight;
            }
            _wordWrap = value;
            _skipWrapAnchor = true;
            RecalcWrapData();
            if (_wordWrap) SetHorizontalOffset(0);
            _textVisualDirty = true;
            _gutterVisualDirty = true;
            UpdateExtent();
            _skipWrapAnchor = false;
            // Restore scroll so the same logical line stays at the top of the viewport
            if (_wordWrap && _wrap.HasValidData(_buffer.Count))
            {
                int newWrap = Math.Min(anchorWrap, VisualLineCount(anchorLine) - 1);
                double newY = (_wrap.CumulOffset(anchorLine) + newWrap) * _font.LineHeight + anchorDelta;
                double maxY = Math.Max(0, _extent.Height - _viewport.Height);
                _offset.Y = Math.Clamp(newY, 0, maxY);
            }
            else
            {
                double newY = anchorLine * _font.LineHeight + anchorDelta;
                double maxY = Math.Max(0, _extent.Height - _viewport.Height);
                _offset.Y = Math.Clamp(newY, 0, maxY);
            }
            ApplyVisualTransforms();
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateVisual();
        }
    }

    private int _caretBlinkMs = 500;
    public int CaretBlinkMs
    {
        get => _caretBlinkMs;
        set
        {
            _caretBlinkMs = value;
            _blinkTimer.Stop();
            if (value > 0)
            {
                _blinkTimer.Interval = TimeSpan.FromMilliseconds(value);
                if (IsKeyboardFocused) _blinkTimer.Start();
            }
            else
            {
                _caretVisible = true;
                InvalidateVisual();
            }
        }
    }

    // ── Rendering constants ──────────────────────────────────────────
    private const double GutterPadding = 4;
    private const double GutterRightMargin = 8;
    private const double GutterSeparatorThickness = 0.5;
    private const double HorizontalScrollPadding = 50;
    private const double BarCaretWidth = 1;
    private const double MouseWheelDeltaUnit = 120.0;
    private const int ScrollWheelLines = 3;

    // ── Cached pens / metrics ───────────────────────────────────────
    private Pen _gutterSepPen = new(Brushes.Gray, GutterSeparatorThickness);
    private int _gutterDigits;

    private readonly Dictionary<int, string> _lineNumStrings = new();
    private static readonly string[] IndentStrings = Enumerable.Range(0, 9).Select(n => new string(' ', n)).ToArray();

    // ── Layered rendering visuals ───────────────────────────────────
    private readonly DrawingVisual _textVisual = new();
    private readonly DrawingVisual _gutterVisual = new();
    private readonly DrawingVisual _caretVisual = new();
    private readonly DrawingVisual _busyVisual = new();
    private readonly DrawingVisual _busySpinnerVisual = new();
    private readonly TranslateTransform _textTransform = new();
    private readonly TranslateTransform _gutterTransform = new();
    private readonly RotateTransform _busySpinnerTransform = new();
    private readonly RectangleGeometry _textClipGeom = new();
    private bool _textVisualDirty = true;
    private bool _gutterVisualDirty = true;
    private int _renderedFirstLine = -1;
    private int _renderedLastLine = -1;
    private int _gutterRenderedFirstLine = -1;
    private int _gutterRenderedLastLine = -1;
    private const int RenderBufferLines = 50;
    private const int LongLineThreshold = 500_000; // skip expensive processing for lines longer than this
    private const int MaxEagerWordWrapLines = 1_000_000;
    // Track rendered scroll region for long-line viewport clamping
    private double _renderedScrollX = double.NaN;
    private double _renderedScrollY = double.NaN;
    // Bias subtracted from content-space X coords to keep values small enough for
    // WPF's float32 render pipeline. Zero for normal files.
    private double _textXBias;
    // Bias subtracted from content-space Y coords so very high line numbers do
    // not lose row-level precision in retained WPF drawing visuals.
    private double _textYBias;
    private double _gutterYBias;

    // ── Font property delegation ─────────────────────────────────────
    public string FontFamilyName
    {
        get => _font.FontFamilyName;
        set => _font.FontFamilyName = value;
    }

    public double EditorFontSize
    {
        get => _font.EditorFontSize;
        set => _font.EditorFontSize = value;
    }

    public double LineHeightMultiplier
    {
        get => _font.LineHeightMultiplier;
        set => _font.LineHeightMultiplier = value;
    }

    public string EditorFontWeight
    {
        get => _font.EditorFontWeight;
        set => _font.EditorFontWeight = value;
    }

    public static List<string> GetMonospaceFonts() => FontManager.GetMonospaceFonts();

    // ── Caret blink ──────────────────────────────────────────────────
    private readonly DispatcherTimer _blinkTimer;
    private bool _caretVisible = true;

    // ── IScrollInfo state ────────────────────────────────────────────
    private Vector _offset;
    private Size _viewport;
    private Size _lastArrangeSize;
    private Size _extent;
    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }

    // ── Busy/read-only operation state ───────────────────────────────
    private bool _isBusy;
    private string _busyMessage = "";
    private double? _busyProgressPercent;
    private bool _busySpinnerAnimating;
    public bool IsBusy => _isBusy;
    internal double? BusyProgressPercent => _busyProgressPercent;

    // ── Word wrap state ──────────────────────────────────────────────
    private readonly WrapLayout _wrap = new();
    private bool _skipWrapAnchor;

    // ── Public API (delegates to buffer) ─────────────────────────────
    public bool IsDirty => _buffer.IsDirty;
    public event EventHandler? DirtyChanged;
    public event EventHandler? CaretMoved;
    public event EventHandler? FindChanged;

    public int CaretLine => _caretLine;
    public int CaretCol => _caretCol;
    public long CharCount => _buffer.CharCount;

    // ── Mouse drag ───────────────────────────────────────────────────
    private bool _isDragging;

    // ── Gutter width (computed) ──────────────────────────────────────
    private double _gutterWidth;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RebuildGutterPen();
        UpdateBusyVisual();
        InvalidateText();
    }

    private void OnFindChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                () => OnFindChanged(sender, e));
            return;
        }

        _textVisualDirty = true;
        InvalidateVisual();
        FindChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Top visible line captured BEFORE font metrics change, used by OnFontChanged.</summary>
    private int _topLineBeforeFontChange;

    private void OnBeforeFontChanged()
    {
        _topLineBeforeFontChange = _font.LineHeight > 0 ? (int)(_offset.Y / _font.LineHeight) : 0;
    }

    private void OnFontChanged()
    {
        _gutterDigits = 0;
        UpdateExtent();

        // Restore scroll position to keep the same line at the top
        double newOffset = _topLineBeforeFontChange * _font.LineHeight;
        newOffset = Math.Clamp(newOffset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        _offset.Y = Math.Round(newOffset * _font.Dpi) / _font.Dpi;
        ScrollOwner?.InvalidateScrollInfo();

        InvalidateText();
    }

    public EditorControl(ThemeManager themeManager, LanguageManager languageManager)
    {
        ThemeManager = themeManager;
        LanguageManager = languageManager;
        _font.BeforeFontChanged += OnBeforeFontChanged;
        _font.FontChanged += OnFontChanged;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;

        _buffer.DirtyChanged += (_, _) => DirtyChanged?.Invoke(this, EventArgs.Empty);
        _find.Changed += OnFindChanged;

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            UpdateCaretVisual();
        };

        _textVisual.Transform = _textTransform;
        _textVisual.Clip = _textClipGeom;
        _gutterVisual.Transform = _gutterTransform;
        _busySpinnerVisual.Transform = _busySpinnerTransform;
        TextOptions.SetTextRenderingMode(_textVisual, TextRenderingMode.ClearType);
        TextOptions.SetTextRenderingMode(_gutterVisual, TextRenderingMode.ClearType);
        TextOptions.SetTextRenderingMode(_busyVisual, TextRenderingMode.ClearType);
        TextOptions.SetTextRenderingMode(_busySpinnerVisual, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(_textVisual, TextHintingMode.Fixed);
        TextOptions.SetTextHintingMode(_gutterVisual, TextHintingMode.Fixed);
        TextOptions.SetTextHintingMode(_busyVisual, TextHintingMode.Fixed);
        TextOptions.SetTextHintingMode(_busySpinnerVisual, TextHintingMode.Fixed);
        AddVisualChild(_textVisual);
        AddVisualChild(_gutterVisual);
        AddVisualChild(_caretVisual);
        AddVisualChild(_busyVisual);
        AddVisualChild(_busySpinnerVisual);

        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged += OnThemeChanged;
            RebuildGutterPen();
            _font.Dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Keyboard.Focus(this);
            _blinkTimer.Start();
            UpdateExtent();
            if (_isBusy)
            {
                UpdateBusyVisual();
                StartBusySpinnerAnimation();
            }
        };

        Unloaded += (_, _) =>
        {
            _blinkTimer.Stop();
            StopBusySpinnerAnimation();
            _font.BeforeFontChanged -= OnBeforeFontChanged;
            _font.FontChanged -= OnFontChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged;
        };
    }

    // ── Visual tree (layered children: text → gutter → caret → busy overlay → busy spinner) ─────
    protected override int VisualChildrenCount => 5;
    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _textVisual,
        1 => _gutterVisual,
        2 => _caretVisual,
        3 => _busyVisual,
        4 => _busySpinnerVisual,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private void InvalidateWrapLayout()
    {
        RecalcWrapData();
        _textVisualDirty = true;
        _gutterVisualDirty = true;
        UpdateExtent();
        InvalidateVisual();
    }

    private void InvalidateText()
    {
        _textVisualDirty = true;
        _gutterVisualDirty = true;
        _renderedFirstLine = -1;
        _renderedLastLine = -1;
        _gutterRenderedFirstLine = -1;
        _gutterRenderedLastLine = -1;
        InvalidateVisual();
    }

    private void UpdateCaretVisual()
    {
        using var dc = _caretVisual.RenderOpen();
        if (_isBusy || !IsKeyboardFocused || !_caretVisible) return;

        var (caretX, caretY) = GetPixelForPosition(_caretLine, _caretCol);
        if (caretX >= _gutterWidth && caretY + _font.LineHeight > 0 && caretY < ActualHeight)
        {
            if (BlockCaret)
            {
                dc.DrawRectangle(ThemeManager.CaretBrush, null,
                    new Rect(caretX, caretY, _font.CharWidth, _font.LineHeight));
                if (_caretCol < LineLength(_caretLine))
                {
                    _font.DrawGlyphRun(dc, LineSegment(_caretLine, _caretCol, 1), 0, 1, caretX, caretY, ThemeManager.EditorBg);
                }
            }
            else
            {
                dc.DrawRectangle(ThemeManager.CaretBrush, null,
                    new Rect(caretX, caretY, BarCaretWidth, _font.LineHeight));
            }
        }
    }

    public void SetBusy(bool isBusy, string? message = null, double? progressPercent = null)
    {
        string nextMessage = isBusy ? message ?? _busyMessage : "";
        double? nextProgressPercent = isBusy && progressPercent.HasValue
            ? Math.Clamp(progressPercent.Value, 0, 100)
            : null;
        if (_isBusy == isBusy
            && string.Equals(_busyMessage, nextMessage, StringComparison.Ordinal)
            && Nullable.Equals(_busyProgressPercent, nextProgressPercent))
            return;

        _isBusy = isBusy;
        _busyMessage = nextMessage;
        _busyProgressPercent = nextProgressPercent;

        if (_isBusy)
        {
            _isDragging = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            _caretVisible = false;
            Cursor = Cursors.Wait;
        }
        else
        {
            Cursor = Cursors.IBeam;
            _caretVisible = true;
        }

        UpdateBusyVisual();
        if (_isBusy)
            StartBusySpinnerAnimation();
        else
            StopBusySpinnerAnimation();
        UpdateCaretVisual();
        InvalidateVisual();
    }

    private void UpdateBusyVisual()
    {
        if (!_isBusy || ActualWidth <= 0 || ActualHeight <= 0)
        {
            using (_busyVisual.RenderOpen()) { }
            using (_busySpinnerVisual.RenderOpen()) { }
            return;
        }

        Brush overlayBrush = CloneWithOpacity(ThemeManager.EditorBg, 0.78);
        Brush panelBrush = CloneWithOpacity(ThemeManager.EditorBg, 0.96);
        Pen borderPen = new(CloneWithOpacity(ThemeManager.GutterFg, 0.55), 1);
        if (borderPen.CanFreeze) borderPen.Freeze();

        string message = string.IsNullOrWhiteSpace(_busyMessage) ? "Working..." : _busyMessage;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string progressText = _busyProgressPercent is { } percent
            ? $"{Math.Clamp(percent, 0, 100):0.0}%"
            : "";
        string displayMessage = progressText.Length > 0 && !message.EndsWith(progressText, StringComparison.Ordinal)
            ? $"{message} {progressText}"
            : message;
        var icon = new FormattedText(
            Codicons.Loading,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(Codicons.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            18,
            ThemeManager.EditorFg,
            dpi);
        var text = new FormattedText(
            displayMessage,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            13,
            ThemeManager.EditorFg,
            dpi);

        bool hasProgress = _busyProgressPercent.HasValue;
        double width = Math.Min(Math.Max(icon.WidthIncludingTrailingWhitespace + text.WidthIncludingTrailingWhitespace + 48,
                hasProgress ? 300 : 220),
            Math.Max(220, ActualWidth - 32));
        double height = hasProgress ? 72 : 48;
        double left = Math.Max(16, (ActualWidth - width) / 2);
        double top = Math.Max(16, (ActualHeight - height) / 2);
        var panel = new Rect(left, top, width, height);

        double contentWidth = icon.WidthIncludingTrailingWhitespace + 10 + text.WidthIncludingTrailingWhitespace;
        double x = left + Math.Max(16, (width - contentWidth) / 2);
        double rowHeight = hasProgress ? 44 : height;
        double iconY = top + (rowHeight - icon.Height) / 2;
        double textY = top + (rowHeight - text.Height) / 2;
        double iconCenterX = x + icon.WidthIncludingTrailingWhitespace / 2;
        double iconCenterY = iconY + icon.Height / 2;
        _busySpinnerTransform.CenterX = iconCenterX;
        _busySpinnerTransform.CenterY = iconCenterY;

        using (var dc = _busyVisual.RenderOpen())
        {
            dc.DrawRectangle(overlayBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
            dc.DrawRoundedRectangle(panelBrush, borderPen, panel, 6, 6);
            dc.DrawText(text, new Point(x + icon.WidthIncludingTrailingWhitespace + 10, textY));

            if (hasProgress)
            {
                double trackLeft = left + 18;
                double trackTop = top + height - 19;
                double trackWidth = Math.Max(1, width - 36);
                var track = new Rect(trackLeft, trackTop, trackWidth, 5);
                var fill = new Rect(trackLeft, trackTop,
                    trackWidth * Math.Clamp(_busyProgressPercent!.Value, 0, 100) / 100.0,
                    track.Height);
                Brush trackBrush = CloneWithOpacity(ThemeManager.GutterFg, 0.28);
                Brush fillBrush = CloneWithOpacity(ThemeManager.SelectionBrush, 0.85);
                dc.DrawRoundedRectangle(trackBrush, null, track, 2.5, 2.5);
                dc.DrawRoundedRectangle(fillBrush, null, fill, 2.5, 2.5);
            }
        }

        using (var dc = _busySpinnerVisual.RenderOpen())
        {
            dc.DrawText(icon, new Point(x, iconY));
        }
    }

    private void StartBusySpinnerAnimation()
    {
        if (_busySpinnerAnimating)
            return;

        var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        _busySpinnerTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
        _busySpinnerAnimating = true;
    }

    private void StopBusySpinnerAnimation()
    {
        _busySpinnerTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        _busySpinnerTransform.Angle = 0;
        _busySpinnerAnimating = false;
    }

    private static Brush CloneWithOpacity(Brush brush, double opacity)
    {
        Brush clone = brush.CloneCurrentValue();
        clone.Opacity = opacity;
        if (clone.CanFreeze) clone.Freeze();
        return clone;
    }

    private static bool IsBusyHandledKey(Key key, bool ctrl, bool alt)
    {
        if (alt && (key == Key.Up || key == Key.Down))
            return true;

        return key switch
        {
            Key.Return or Key.Back or Key.Delete or Key.Tab => true,
            Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown => true,
            Key.A or Key.C or Key.X or Key.V or Key.Z or Key.Y or Key.D or Key.K or Key.L when ctrl => true,
            _ => false
        };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _font.Dpi = newDpi.PixelsPerDip;
        InvalidateText();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Undo / Redo helpers (region-based)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>Snapshot of the buffer region before an edit, used by BeginEdit/EndEdit.</summary>
    private readonly record struct EditScope(
        int StartLine, int LineCount, int BufferCount,
        TextBuffer.LineSnapshot Before, int CaretLine, int CaretCol);

    private EditScope BeginEdit(int startLine, int endLine)
    {
        int safeStart = Math.Clamp(startLine, 0, Math.Max(0, _buffer.Count - 1));
        int safeEnd = Math.Clamp(endLine, safeStart, Math.Max(0, _buffer.Count - 1));
        int count = safeEnd - safeStart + 1;
        return new EditScope(safeStart, count, _buffer.Count,
            _buffer.SnapshotLines(safeStart, count), _caretLine, _caretCol);
    }

    private void EndEdit(EditScope scope)
    {
        int lineDelta = _buffer.Count - scope.BufferCount;
        int afterStart = Math.Min(scope.StartLine, _buffer.Count);
        int afterCount = Math.Clamp(scope.LineCount + lineDelta, 0, _buffer.Count - afterStart);
        var after = _buffer.SnapshotLines(afterStart, afterCount);
        bool evicted = _undoManager.Push(new UndoManager.UndoEntry(
            scope.StartLine, scope.Before, after,
            scope.CaretLine, scope.CaretCol, _caretLine, _caretCol));
        MarkEditDirty(evicted, scope.StartLine);
    }

    /// <summary>
    /// Shared post-edit bookkeeping: update dirty flags, line states, and clean depth.
    /// Called after region-based edits are captured by the undo manager.
    /// </summary>
    private void MarkEditDirty(bool undoEntryEvicted, int dirtyFromLine)
    {
        if (undoEntryEvicted && _cleanUndoDepth >= 0)
            _cleanUndoDepth--;
        _textVisualDirty = true;
        InvalidateLanguageAnalysis();
        if (_find.HasQuery)
            _find.InvalidateForEdit();
        _buffer.IsDirty = true;
    }

    /// <summary>
    /// Returns the line range affected by the current selection, or (caretLine, caretLine) if none.
    /// </summary>
    private (int start, int end) GetEditRange()
    {
        if (!_selection.HasSelection) return (_caretLine, _caretLine);
        var (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
        return (sl, el);
    }

    private void DeleteSelectionIfPresent()
    {
        if (_selection.HasSelection)
            (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
    }

    private void FinishEdit(EditScope scope)
    {
        EndEdit(scope);
        _selection.Clear();
        UpdateExtent();
        EnsureCaretVisible();
        ResetCaret();
    }

    private void Undo()
    {
        var entry = _undoManager.Undo();
        if (entry == null) return;

        switch (entry)
        {
            case UndoManager.UndoEntry ue:
                {
                    _buffer.ReplaceLines(ue.StartLine, ue.After.Count, ue.Before);
                    break;
                }
        }

        FinishUndoRedo(entry.CaretLineBefore, entry.CaretColBefore);
    }

    private void Redo()
    {
        var entry = _undoManager.Redo();
        if (entry == null) return;

        switch (entry)
        {
            case UndoManager.UndoEntry ue:
                {
                    _buffer.ReplaceLines(ue.StartLine, ue.Before.Count, ue.After);
                    break;
                }
        }

        FinishUndoRedo(entry.CaretLineAfter, entry.CaretColAfter);
    }

    private void FinishUndoRedo(int caretLine, int caretCol)
    {
        _caretLine = caretLine;
        _caretCol = caretCol;
        ClampCaret();
        _selection.Clear();
        InvalidateLanguageAnalysis();
        _buffer.IsDirty = _undoManager.UndoCount != _cleanUndoDepth;
        UpdateExtent();
        InvalidateText();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Hit testing (pixel → line/col)
    // ──────────────────────────────────────────────────────────────────
    private (int line, int col) HitTest(Point pos)
    {
        if (!_wordWrap)
        {
            int line = (int)((pos.Y + _offset.Y) / _font.LineHeight);
            line = Math.Clamp(line, 0, _buffer.Count - 1);
            double textX = pos.X + _offset.X - _gutterWidth - GutterPadding;
            int col = (int)Math.Round(textX / _font.CharWidth);
            col = Math.Clamp(col, 0, LineLength(line));
            return (line, col);
        }

        int visualLine = (int)((pos.Y + _offset.Y) / _font.LineHeight);
        visualLine = Math.Clamp(visualLine, 0, _wrap.TotalVisualLines - 1);
        var (logLine, wrapIndex) = VisualToLogical(visualLine);

        double indentPx = WrapIndentPx(logLine, wrapIndex);
        double tx = pos.X - _gutterWidth - GutterPadding - indentPx;
        int colInWrap = (int)Math.Round(tx / _font.CharWidth);
        colInWrap = Math.Max(0, colInWrap);
        int col2 = WrapColStart(logLine, wrapIndex) + colInWrap;
        col2 = Math.Clamp(col2, 0, LineLength(logLine));
        return (logLine, col2);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Word boundary helpers
    // ──────────────────────────────────────────────────────────────────
    private static int WordLeft(string line, int col)
    {
        if (col <= 0) return 0;
        int i = col - 1;
        while (i > 0 && char.IsWhiteSpace(line[i])) i--;
        while (i > 0 && !char.IsWhiteSpace(line[i - 1]) && !IsPunctuation(line[i - 1])) i--;
        return i;
    }

    private static int WordRight(string line, int col)
    {
        if (col >= line.Length) return line.Length;
        int i = col;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        while (i < line.Length && !char.IsWhiteSpace(line[i]) && !IsPunctuation(line[i])) i++;
        return i;
    }

    private static bool IsPunctuation(char c) =>
        char.IsPunctuation(c) || char.IsSymbol(c);

    private (int start, int end) GetWordAt(int line, int col)
    {
        var text = LineSegment(line, 0, Math.Min(LineLength(line), LongLineThreshold));
        if (text.Length == 0) return (0, 0);
        col = Math.Clamp(col, 0, Math.Max(0, text.Length - 1));

        int start = col, end = col;
        if (char.IsLetterOrDigit(text[col]) || text[col] == '_')
        {
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        }
        else
        {
            end = col + 1;
        }
        return (start, end);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Scroll / extent management
    // ──────────────────────────────────────────────────────────────────
    private void UpdateExtent()
    {
        // Anchor scroll to the top visible logical line across wrap recalculations
        int anchorLine = -1;
        int anchorWrap = 0;
        double anchorDelta = 0;
        if (_wordWrap && !_skipWrapAnchor && _wrap.HasValidData(_buffer.Count) && _wrap.TotalVisualLines > 0)
        {
            int topVisual = Math.Clamp((int)(_offset.Y / _font.LineHeight), 0, _wrap.TotalVisualLines - 1);
            (anchorLine, anchorWrap) = VisualToLogical(topVisual);
            anchorDelta = _offset.Y - (_wrap.CumulOffset(anchorLine) + anchorWrap) * _font.LineHeight;
        }

        RecalcWrapData();

        // Restore scroll position so the same logical line stays at top
        if (_wordWrap && anchorLine >= 0 && _wrap.HasValidData(_buffer.Count))
        {
            int newWrap = Math.Min(anchorWrap, VisualLineCount(anchorLine) - 1);
            double newY = (_wrap.CumulOffset(anchorLine) + newWrap) * _font.LineHeight + anchorDelta;
            double maxY = Math.Max(0, _wrap.TotalVisualLines * _font.LineHeight + _viewport.Height / 2 - _viewport.Height);
            _offset.Y = Math.Clamp(newY, 0, maxY);
            ApplyVisualTransforms();
        }

        int digits = _buffer.Count > 0
            ? (int)Math.Floor(Math.Log10(_buffer.Count)) + 1
            : 1;
        if (digits != _gutterDigits)
        {
            _gutterDigits = digits;
            _gutterWidth = digits * _font.CharWidth + GutterRightMargin;
        }

        int maxLen = _buffer.UpdateMaxForLine(_caretLine);

        var newExtent = new Size(
            _wordWrap
                ? _viewport.Width
                : _gutterWidth + GutterPadding + maxLen * _font.CharWidth + HorizontalScrollPadding,
            (_wordWrap ? _wrap.TotalVisualLines : _buffer.Count) * _font.LineHeight + _viewport.Height / 2);

        if (Math.Abs(newExtent.Width - _extent.Width) > 0.5
            || Math.Abs(newExtent.Height - _extent.Height) > 0.5)
        {
            _extent = newExtent;
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private void RecalcWrapData()
    {
        double textAreaWidth = _viewport.Width - _gutterWidth - GutterPadding;
        _wrap.Recalculate(_wordWrap, _wordWrapAtWords, _wordWrapIndent, _buffer, textAreaWidth, _font.CharWidth);
    }

    // ── Wrap coordinate helpers (delegate to WrapLayout) ────────────
    private int LogicalToVisualLine(int logLine, int col = 0) =>
        _wrap.LogicalToVisualLine(_wordWrap, logLine, col);

    private double GetVisualY(int logLine, int col = 0) =>
        _wrap.GetVisualY(_wordWrap, logLine, _font.LineHeight, col);

    private double GetLineTopY(int logLine) =>
        (_wordWrap ? _wrap.CumulOffset(logLine) : logLine) * _font.LineHeight;

    private void ApplyVisualTransforms()
    {
        _textTransform.X = -(_offset.X - _textXBias);
        _textTransform.Y = -(_offset.Y - _textYBias);
        _gutterTransform.Y = -(_offset.Y - _gutterYBias);
    }

    private (int logLine, int wrapIndex) VisualToLogical(int visualLine) =>
        _wrap.VisualToLogical(_wordWrap, visualLine, _buffer.Count);

    private int VisualLineCount(int logLine) =>
        _wrap.VisualLineCount(_wordWrap, logLine);

    private int WrapColStart(int logLine, int wrapIndex) =>
        _wrap.WrapColStart(_wordWrap, logLine, wrapIndex);

    private double WrapIndentPx(int logLine, int wrapIndex) =>
        _wrap.WrapIndentPx(_wordWrap, logLine, wrapIndex, _font.CharWidth);

    private (double x, double y) GetPixelForPosition(int line, int col) =>
        _wrap.GetPixelForPosition(_wordWrap, line, col, _gutterWidth, GutterPadding,
            _font.CharWidth, _font.LineHeight, _offset.X, _offset.Y);

    private int LineLength(int line) => _buffer.GetLineLength(line);

    private string LineSegment(int line, int startColumn, int length) =>
        _buffer.GetLineSegment(line, startColumn, length);

    private int CountLeadingSpacesToRemove(int line)
    {
        int inspect = Math.Min(TabSize, LineLength(line));
        return LeadingSpaceRemovalTextSource.CountLeadingSpaces(LineSegment(line, 0, inspect), inspect);
    }

    private bool ShouldSuppressWordWrap() =>
        _buffer.Count > MaxEagerWordWrapLines || _buffer.MaxLineLength > LongLineThreshold;

    private void SuppressWordWrapIfNeeded()
    {
        if (_wordWrap && ShouldSuppressWordWrap())
            _wordWrap = false;
    }

    private void EnsureCaretVisible()
    {
        double caretTop = GetVisualY(_caretLine, _caretCol);
        double caretBottom = caretTop + _font.LineHeight;
        if (caretTop < _offset.Y)
            SetVerticalOffset(caretTop);
        else if (caretBottom > _offset.Y + _viewport.Height)
            SetVerticalOffset(caretBottom - _viewport.Height);

        if (!_wordWrap)
        {
            double caretX = _gutterWidth + GutterPadding + _caretCol * _font.CharWidth;
            if (caretX - _offset.X < _gutterWidth + GutterPadding)
                SetHorizontalOffset(caretX - _gutterWidth - GutterPadding);
            else if (caretX - _offset.X > _viewport.Width - _font.CharWidth)
                SetHorizontalOffset(caretX - _viewport.Width + _font.CharWidth * 2);
        }
    }

    private void ClampCaret()
    {
        _caretLine = Math.Clamp(_caretLine, 0, Math.Max(0, _buffer.Count - 1));
        _caretCol = Math.Clamp(_caretCol, 0, LineLength(_caretLine));
    }

    private void ResetPreferredCol() => _preferredCol = -1;

    private void ResetCaret()
    {
        _caretVisible = true;
        _blinkTimer.Stop();
        if (_caretBlinkMs > 0) _blinkTimer.Start();
        if (_caretLine != _prevCaretLine)
        {
            _gutterVisualDirty = true;
            _prevCaretLine = _caretLine;
        }
        InvalidateVisual();
        CaretMoved?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildGutterPen()
    {
        _gutterSepPen = new Pen(ThemeManager.GutterFg, GutterSeparatorThickness);
        _gutterSepPen.Freeze();
    }

    private void InvalidateLanguageAnalysis()
    {
        _languageSnapshot = null;
        _languageSnapshotGeneration = -1;
    }

    public LanguageSnapshot? GetLanguageSnapshot()
    {
        if (_languageService == null)
            return null;

        long generation = _buffer.EditGeneration;
        if (_languageSnapshot != null && _languageSnapshotGeneration == generation)
            return _languageSnapshot;

        _languageSnapshot = _languageService.Analyze(GetContent(), generation);
        _languageSnapshotGeneration = generation;
        return _languageSnapshot;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Rendering (layered: background → text → gutter → caret)
    // ──────────────────────────────────────────────────────────────────
    private (int first, int last) VisibleLineRange()
    {
        if (!_wordWrap)
        {
            int first = Math.Max(0, (int)(_offset.Y / _font.LineHeight));
            int last = Math.Min(_buffer.Count - 1,
                (int)((_offset.Y + _viewport.Height) / _font.LineHeight));
            return (first, last);
        }

        int firstVisual = Math.Max(0, (int)(_offset.Y / _font.LineHeight));
        int lastVisual = (int)((_offset.Y + _viewport.Height) / _font.LineHeight);
        lastVisual = Math.Min(lastVisual, _wrap.TotalVisualLines - 1);
        if (_wrap.TotalVisualLines == 0) return (0, Math.Max(0, _buffer.Count - 1));

        var (firstLog, _) = VisualToLogical(firstVisual);
        var (lastLog, _) = VisualToLogical(lastVisual);
        return (firstLog, lastLog);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_isBusy)
        {
            dc.DrawRectangle(ThemeManager.EditorBg, null,
                new Rect(0, 0, ActualWidth, ActualHeight));
            UpdateCaretVisual();
            UpdateBusyVisual();
            return;
        }

        ClampCaret();

        var (firstLine, lastLine) = VisibleLineRange();

        dc.DrawRectangle(ThemeManager.EditorBg, null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        if (_caretLine >= firstLine && _caretLine <= lastLine)
        {
            double curLineY = GetVisualY(_caretLine, _caretCol) - _offset.Y;
            dc.DrawRectangle(ThemeManager.CurrentLineBrush, null,
                new Rect(0, curLineY, ActualWidth, _font.LineHeight));
        }

        if (_selection.HasSelection)
        {
            var (sl, sc, el, ec) = _selection.GetOrdered(_caretLine, _caretCol);
            for (int i = Math.Max(firstLine, sl); i <= Math.Min(lastLine, el); i++)
            {
                int selStart = i == sl ? sc : 0;
                int selEnd = i == el ? ec : LineLength(i);

                if (!_wordWrap)
                {
                    double y = i * _font.LineHeight - _offset.Y;
                    double x1 = _gutterWidth + GutterPadding + selStart * _font.CharWidth - _offset.X;
                    double x2 = _gutterWidth + GutterPadding + selEnd * _font.CharWidth - _offset.X;
                    if (i > sl && i < el) x2 = Math.Max(x2, ActualWidth);
                    if (i == sl && i != el) x2 = Math.Max(x2, ActualWidth);
                    dc.DrawRectangle(ThemeManager.SelectionBrush, null,
                        new Rect(Math.Max(x1, _gutterWidth + GutterPadding), y,
                                 Math.Max(0, x2 - Math.Max(x1, _gutterWidth + GutterPadding)),
                                 _font.LineHeight));
                }
                else
                {
                    RenderWrappedSelection(dc, i, selStart, selEnd, sl, sc, el);
                }
            }
        }

        if (_find.HasQuery)
        {
            int firstFindColumn = 0;
            int lastFindColumn = int.MaxValue;
            if (!_wordWrap && _font.CharWidth > 0)
            {
                double textWidth = Math.Max(0, _viewport.Width - _gutterWidth - GutterPadding);
                firstFindColumn = Math.Max(0, (int)Math.Floor(_offset.X / _font.CharWidth) - 2);
                lastFindColumn = Math.Max(firstFindColumn,
                    (int)Math.Ceiling((_offset.X + textWidth) / _font.CharWidth) + 2);
            }

            var currentMatch = _find.GetCurrentMatch();
            foreach (var (mLine, mCol, mLen) in _find.GetMatchesInRange(
                         firstLine, lastLine, firstFindColumn, lastFindColumn))
            {
                bool isCurrent = currentMatch.HasValue
                                 && currentMatch.Value.Line == mLine
                                 && currentMatch.Value.Col == mCol
                                 && currentMatch.Value.Length == mLen;
                var brush = isCurrent
                    ? ThemeManager.FindMatchCurrentBrush
                    : ThemeManager.FindMatchBrush;

                if (!_wordWrap)
                {
                    double pxStart = mCol * _font.CharWidth;
                    double pxEnd = (mCol + mLen) * _font.CharWidth;
                    double mx = _gutterWidth + GutterPadding + pxStart - _offset.X;
                    double my = mLine * _font.LineHeight - _offset.Y;
                    double textLeft = _gutterWidth + GutterPadding;
                    double clippedX = Math.Max(mx, textLeft);
                    double clippedW = Math.Max(0, mx + (pxEnd - pxStart) - clippedX);
                    if (clippedW > 0)
                        dc.DrawRectangle(brush, null,
                            new Rect(clippedX, my, clippedW, _font.LineHeight));
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
                        int wrapEnd = wrapIndex + 1 < vCount ? WrapColStart(mLine, wrapIndex + 1) : LineLength(mLine);
                        int charsOnThisLine = Math.Min(remaining, wrapEnd - col);
                        double indentPx = WrapIndentPx(mLine, wrapIndex);
                        double mx = _gutterWidth + GutterPadding + indentPx + colInWrap * _font.CharWidth;
                        double my = visLine * _font.LineHeight - _offset.Y;
                        dc.DrawRectangle(brush, null,
                            new Rect(mx, my, charsOnThisLine * _font.CharWidth, _font.LineHeight));
                        col += charsOnThisLine;
                        remaining -= charsOnThisLine;
                    }
                }
            }
        }

        // For long lines, the rendered region is clamped to the viewport.
        // Re-render when scroll moves beyond the rendered buffer zone.
        bool longLineScrolled = !double.IsNaN(_renderedScrollX)
            && (Math.Abs(_offset.X - _renderedScrollX) > _viewport.Width * 0.25
                || Math.Abs(_offset.Y - _renderedScrollY) > _viewport.Height * 0.25);

        if (_textVisualDirty
            || firstLine < _renderedFirstLine
            || lastLine > _renderedLastLine
            || longLineScrolled)
        {
            RenderTextVisual(firstLine, lastLine);
            _textVisualDirty = false;
        }
        if (_gutterVisualDirty
            || firstLine < _gutterRenderedFirstLine
            || lastLine > _gutterRenderedLastLine)
        {
            RenderGutterVisual(firstLine, lastLine);
            _gutterVisualDirty = false;
        }

        // Set transform/clip AFTER rendering so render biases are up to date.
        ApplyVisualTransforms();

        _textClipGeom.Rect = new Rect(
            _gutterWidth + _offset.X - _textXBias, _offset.Y - _textYBias,
            Math.Max(0, ActualWidth - _gutterWidth), ActualHeight);
        UpdateCaretVisual();
        UpdateBusyVisual();
    }

    private void RenderWrappedSelection(DrawingContext dc, int line, int selStart, int selEnd, int sl, int sc, int el)
    {
        int vCount = VisualLineCount(line);
        int baseCumul = _wrap.CumulOffset(line);
        int visFirst = Math.Max(0, (int)(_offset.Y / _font.LineHeight) - RenderBufferLines - baseCumul);
        int visLast = Math.Min(vCount - 1,
            (int)((_offset.Y + _viewport.Height) / _font.LineHeight) + RenderBufferLines - baseCumul);
        for (int w = Math.Max(0, visFirst); w <= visLast; w++)
        {
            int wStart = WrapColStart(line, w);
            int wEnd = w + 1 < vCount ? WrapColStart(line, w + 1) : LineLength(line);
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

            dc.DrawRectangle(ThemeManager.SelectionBrush, null,
                new Rect(Math.Max(x1, _gutterWidth + GutterPadding), y,
                         Math.Max(0, x2 - Math.Max(x1, _gutterWidth + GutterPadding)),
                         _font.LineHeight));
        }
    }

    private void RenderTextVisual(int firstLine, int lastLine)
    {
        int drawFirst = Math.Max(0, firstLine - RenderBufferLines);
        int drawLast = Math.Min(_buffer.Count - 1, lastLine + RenderBufferLines);

        using var dc = _textVisual.RenderOpen();

        if (drawLast < drawFirst) return;

        // Compute X bias before rendering. When long lines are visible, bias
        // shifts content-space X origin near the viewport to avoid float32
        // precision loss in WPF's transform pipeline at very large pixel offsets.
        bool hasLongLine = false;
        for (int i = drawFirst; i <= drawLast; i++)
            if (LineLength(i) > LongLineThreshold) { hasLongLine = true; break; }
        _textXBias = hasLongLine ? _offset.X : 0;
        _textYBias = GetLineTopY(drawFirst);

        for (int i = drawFirst; i <= drawLast; i++)
        {
            int lineLength = LineLength(i);
            if (lineLength == 0) continue;
            double x = _gutterWidth + GutterPadding;

            bool longLine = lineLength > LongLineThreshold;
            string line = longLine ? "" : _buffer[i];

            if (!_wordWrap || VisualLineCount(i) <= 1)
            {
                double y = GetLineTopY(i) - _textYBias;
                int segStart = 0;
                int segEnd = lineLength;
                string drawText = line;
                if (longLine)
                {
                    // Clamp to visible horizontal range to avoid rendering millions of off-screen chars
                    segStart = Math.Max(0, (int)(_offset.X / _font.CharWidth) - 2);
                    segEnd = Math.Min(lineLength,
                        (int)((_offset.X + _viewport.Width) / _font.CharWidth) + 2);
                    drawText = LineSegment(i, segStart, segEnd - segStart);
                    _font.DrawGlyphRun(dc, drawText, 0, drawText.Length,
                        x + segStart * _font.CharWidth - _textXBias, y, ThemeManager.EditorFg);
                    continue;
                }
                // Subtract _textXBias from X to keep content-space coords small
                _font.DrawGlyphRun(dc, drawText, segStart, segEnd - segStart,
                    x + segStart * _font.CharWidth - _textXBias, y, ThemeManager.EditorFg);
            }
            else
            {
                int vCount = VisualLineCount(i);
                int baseCumul = _wrap.CumulOffset(i);
                // Clamp wrap segment loop to viewport-visible range (critical for very long lines)
                int visFirst = Math.Max(0, (int)(_offset.Y / _font.LineHeight) - RenderBufferLines - baseCumul);
                int visLast = Math.Min(vCount - 1,
                    (int)((_offset.Y + _viewport.Height) / _font.LineHeight) + RenderBufferLines - baseCumul);
                for (int w = Math.Max(0, visFirst); w <= visLast; w++)
                {
                    int segStart = WrapColStart(i, w);
                    int segEnd = w + 1 < vCount ? WrapColStart(i, w + 1) : lineLength;
                    double y = (baseCumul + w) * _font.LineHeight - _textYBias;
                    double wx = x + WrapIndentPx(i, w);
                    string drawText = longLine ? LineSegment(i, segStart, segEnd - segStart) : line;
                    _font.DrawGlyphRun(dc, drawText, longLine ? 0 : segStart,
                        longLine ? drawText.Length : segEnd - segStart, wx, y, ThemeManager.EditorFg);
                }
            }
        }

        _renderedFirstLine = drawFirst;
        _renderedLastLine = drawLast;
        // Track scroll position when long-line clamping is active
        if (hasLongLine)
        {
            _renderedScrollX = _offset.X;
            _renderedScrollY = _offset.Y;
        }
        else
        {
            _renderedScrollX = double.NaN;
        }

    }

    // ──────────────────────────────────────────────────────────────────
    //  Navigation
    // ──────────────────────────────────────────────────────────────────

    public void GoToLine(int line)
    {
        _selection.Clear();
        _caretLine = Math.Clamp(line, 0, _buffer.Count - 1);
        _caretCol = 0;
        ResetPreferredCol();
        // Centre the target line in the viewport
        double caretTop = GetVisualY(_caretLine, _caretCol);
        double target = caretTop - (_viewport.Height - _font.LineHeight) / 2;
        SetVerticalOffset(Math.Max(0, target));
        ResetCaret();
        _textVisualDirty = true;
        InvalidateVisual();
    }

    /// <summary>Find the next visible line at or after the given line.</summary>
    private int NextVisibleLine(int line) => Math.Min(line, _buffer.Count - 1);

    /// <summary>Find the previous visible line at or before the given line.</summary>
    private static int PrevVisibleLine(int line) => Math.Max(line, 0);

    private void RenderGutterVisual(int firstLine, int lastLine)
    {
        int drawFirst = Math.Max(0, firstLine - RenderBufferLines);
        int drawLast = Math.Min(_buffer.Count - 1, lastLine + RenderBufferLines);

        using var dc = _gutterVisual.RenderOpen();

        if (drawLast < drawFirst) return;

        _gutterYBias = GetLineTopY(drawFirst);

        double bgTop, bgBottom;
        if (_wordWrap)
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

        dc.DrawRectangle(ThemeManager.EditorBg, null,
            new Rect(0, bgTop, _gutterWidth, bgBottom - bgTop));
        dc.DrawLine(_gutterSepPen,
            new Point(_gutterWidth, bgTop), new Point(_gutterWidth, bgBottom));

        for (int i = drawFirst; i <= drawLast; i++)
        {
            double y = _wordWrap
                ? _wrap.CumulOffset(i) * _font.LineHeight
                : i * _font.LineHeight;
            y -= _gutterYBias;
            var brush = i == _caretLine
                ? ThemeManager.ActiveLineNumberFg : ThemeManager.GutterFg;
            int lineNum = i + 1;
            if (!_lineNumStrings.TryGetValue(lineNum, out var numStr))
            {
                numStr = lineNum.ToString();
                _lineNumStrings[lineNum] = numStr;
            }
            double numWidth = numStr.Length * _font.CharWidth;
            _font.DrawGlyphRun(dc, numStr, 0, numStr.Length,
                _gutterWidth - numWidth - GutterPadding, y, brush);
        }

        _gutterRenderedFirstLine = drawFirst;
        _gutterRenderedLastLine = drawLast;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Focus
    // ──────────────────────────────────────────────────────────────────
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _caretVisible = true;
        if (_caretBlinkMs > 0) _blinkTimer.Start();
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _blinkTimer.Stop();
        _caretVisible = false;
        InvalidateVisual();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Mouse
    // ──────────────────────────────────────────────────────────────────
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_isBusy)
        {
            e.Handled = true;
            return;
        }

        Focus();
        Keyboard.Focus(this);

        var pos = e.GetPosition(this);

        CaptureMouse();
        var (line, col) = HitTest(pos);

        if (e.ClickCount == 2)
        {
            var (ws, we) = GetWordAt(line, col);
            _caretLine = line;
            _selection.SetAnchor(line, ws);
            _caretCol = we;
            _selection.HasSelection = ws != we;
        }
        else
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                _selection.Start(_caretLine, _caretCol);
            }
            else
            {
                _selection.Clear();
                _selection.SetAnchor(line, col);
            }
            _caretLine = line;
            _caretCol = col;
        }
        _isDragging = true;
        ResetPreferredCol();
        ResetCaret();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isBusy)
        {
            Cursor = Cursors.Wait;
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(this);

        if (!_isDragging)
            Cursor = pos.X < _gutterWidth ? Cursors.Arrow : Cursors.IBeam;

        if (!_isDragging) return;
        var (line, col) = HitTest(pos);

        if (line == _caretLine && col == _caretCol)
        {
            e.Handled = true;
            return;
        }

        if (line != _selection.AnchorLine || col != _selection.AnchorCol)
            _selection.HasSelection = true;

        _caretLine = line;
        _caretCol = col;
        EnsureCaretVisible();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_isBusy)
        {
            e.Handled = true;
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        Cursor = Cursors.IBeam;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_isBusy)
        {
            e.Handled = true;
            return;
        }

        Focus();
        var menu = ContextMenuHelper.Create();

        bool hasSel = _selection.HasSelection;
        bool hasClip;
        try { hasClip = Clipboard.ContainsText(); }
        catch (System.Runtime.InteropServices.ExternalException) { hasClip = false; }

        if (hasSel)
        {
            var cut = ContextMenuHelper.Item("Cut", Codicons.ScreenCut, HandleCut);
            cut.InputGestureText = "Ctrl+X";
            menu.Items.Add(cut);

            var copy = ContextMenuHelper.Item("Copy", Codicons.Copy, HandleCopy);
            copy.InputGestureText = "Ctrl+C";
            menu.Items.Add(copy);
        }

        if (hasClip)
        {
            var paste = ContextMenuHelper.Item("Paste", Codicons.Clippy, HandlePaste);
            paste.InputGestureText = "Ctrl+V";
            menu.Items.Add(paste);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new System.Windows.Controls.Separator());

        var selectAll = ContextMenuHelper.Item("Select All", Codicons.ListSelection, HandleSelectAll);
        selectAll.InputGestureText = "Ctrl+A";
        menu.Items.Add(selectAll);

        ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Text input (printable characters)
    // ──────────────────────────────────────────────────────────────────
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (_isBusy)
        {
            e.Handled = true;
            return;
        }

        if (string.IsNullOrEmpty(e.Text) || e.Text[0] < ' ') return;

        ResetPreferredCol();
        var (sl, el) = GetEditRange();
        var scope = BeginEdit(sl, el);
        DeleteSelectionIfPresent();

        _buffer.InsertAt(_caretLine, _caretCol, e.Text);
        _caretCol += e.Text.Length;

        FinishEdit(scope);
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Keyboard — dispatch to handler methods
    // ──────────────────────────────────────────────────────────────────
    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_isBusy)
        {
            if (IsBusyHandledKey(key, ctrl, alt))
                e.Handled = true;
            return;
        }

        // Alt+key arrives as Key.System — check SystemKey for the real key
        if (e.Key == Key.System && alt && !ctrl)
        {
            switch (e.SystemKey)
            {
                case Key.Up:
                    HandleMoveLine(true);
                    e.Handled = true;
                    return;
                case Key.Down:
                    HandleMoveLine(false);
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Return when ctrl && shift:
                HandleInsertBlankLine(above: true);
                e.Handled = true;
                break;

            case Key.Return when ctrl:
                HandleInsertBlankLine(above: false);
                e.Handled = true;
                break;

            case Key.Return:
                HandleReturn();
                e.Handled = true;
                break;

            case Key.Back:
                HandleBackspace();
                e.Handled = true;
                break;

            case Key.Delete:
                HandleDelete();
                e.Handled = true;
                break;

            case Key.Tab:
                HandleTab(shift);
                e.Handled = true;
                break;

            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
            case Key.Home:
            case Key.End:
            case Key.PageUp:
            case Key.PageDown:
                HandleNavigation(e.Key, shift, ctrl);
                e.Handled = true;
                break;

            case Key.A when ctrl:
                HandleSelectAll();
                e.Handled = true;
                break;

            case Key.C when ctrl:
                HandleCopy();
                e.Handled = true;
                break;

            case Key.X when ctrl:
                HandleCut();
                e.Handled = true;
                break;

            case Key.V when ctrl:
                HandlePaste();
                e.Handled = true;
                break;

            case Key.Z when ctrl:
                ResetPreferredCol();
                Undo();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.Y when ctrl:
                ResetPreferredCol();
                Redo();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.D when ctrl:
                HandleDuplicateLine();
                e.Handled = true;
                break;

            case Key.K when ctrl && shift:
                HandleDeleteLine();
                e.Handled = true;
                break;

            case Key.L when ctrl:
                HandleSelectLine();
                e.Handled = true;
                break;

        }
    }

    // ── Key handlers ─────────────────────────────────────────────────

    private void HandleReturn()
    {
        ResetPreferredCol();
        var (sl, el) = GetEditRange();
        var scope = BeginEdit(sl, el);
        DeleteSelectionIfPresent();

        var currentLine = _buffer[_caretLine];
        var indent = currentLine[..(currentLine.Length - currentLine.TrimStart().Length)];
        var rest = _buffer.TruncateAt(_caretLine, _caretCol);

        _caretLine++;
        _buffer.InsertLine(_caretLine, indent + rest);
        _caretCol = indent.Length;

        FinishEdit(scope);
    }

    private void HandleBackspace()
    {
        ResetPreferredCol();
        var (sl, el) = GetEditRange();
        if (!_selection.HasSelection && _caretCol == 0 && _caretLine > 0)
            sl = _caretLine - 1;
        var scope = BeginEdit(sl, el);

        if (_selection.HasSelection)
        {
            (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
        }
        else if (_caretCol > 0)
        {
            var line = _buffer[_caretLine];

            int leadingSpaces = line.Length - line.TrimStart().Length;
            int remove = 1;
            if (_caretCol <= leadingSpaces && line.AsSpan(0, _caretCol).IndexOfAnyExcept(' ') < 0)
            {
                int prevStop = (_caretCol - 1) / TabSize * TabSize;
                remove = _caretCol - prevStop;
            }
            _buffer.DeleteAt(_caretLine, _caretCol - remove, remove);
            _caretCol -= remove;
        }
        else if (_caretLine > 0)
        {
            _caretCol = LineLength(_caretLine - 1);
            _buffer.JoinWithNext(_caretLine - 1);
            _caretLine--;
        }
        FinishEdit(scope);
    }

    private void HandleDelete()
    {
        ResetPreferredCol();
        var (sl, el) = GetEditRange();
        if (!_selection.HasSelection && _caretCol >= LineLength(_caretLine) && _caretLine < _buffer.Count - 1)
            el = _caretLine + 1;
        var scope = BeginEdit(sl, el);

        if (_selection.HasSelection)
        {
            (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
        }
        else if (_caretCol < LineLength(_caretLine))
        {
            _buffer.DeleteAt(_caretLine, _caretCol, 1);
        }
        else if (_caretLine < _buffer.Count - 1)
        {
            _buffer.JoinWithNext(_caretLine);
        }
        FinishEdit(scope);
    }

    private void HandleTab(bool shift)
    {
        ResetPreferredCol();
        var (sl, el) = GetEditRange();

        if (_selection.HasSelection && sl != el)
        {
            int lineCount = el - sl + 1;
            var indentScope = BeginEdit(sl, el);

            if (!shift)
            {
                string prefix = TabSize < IndentStrings.Length
                    ? IndentStrings[TabSize]
                    : new string(' ', TabSize);

                _buffer.AddPrefixToLines(sl, lineCount, prefix);
                if (_caretLine >= sl && _caretLine <= el) _caretCol += TabSize;
                if (_selection.AnchorLine >= sl && _selection.AnchorLine <= el) _selection.AnchorCol += TabSize;
            }
            else
            {
                int caretRemove = _caretLine >= sl && _caretLine <= el ? CountLeadingSpacesToRemove(_caretLine) : 0;
                int anchorRemove = _selection.AnchorLine >= sl && _selection.AnchorLine <= el
                    ? CountLeadingSpacesToRemove(_selection.AnchorLine)
                    : 0;

                _buffer.RemoveLeadingSpacesFromLines(sl, lineCount, TabSize);
                if (_caretLine >= sl && _caretLine <= el) _caretCol = Math.Max(0, _caretCol - caretRemove);
                if (_selection.AnchorLine >= sl && _selection.AnchorLine <= el)
                    _selection.AnchorCol = Math.Max(0, _selection.AnchorCol - anchorRemove);
            }

            EndEdit(indentScope);
            _selection.HasSelection = true;
            UpdateExtent();
            EnsureCaretVisible();
            ResetCaret();
            return;
        }

        if (shift) return;

        var scope = BeginEdit(sl, el);
        DeleteSelectionIfPresent();
        int spacesToInsert = TabSize - (_caretCol % TabSize);
        _buffer.InsertAt(_caretLine, _caretCol, new string(' ', spacesToInsert));
        _caretCol += spacesToInsert;
        FinishEdit(scope);
    }

    private void HandleNavigation(Key key, bool shift, bool ctrl)
    {
        switch (key)
        {
            case Key.Left:
                ResetPreferredCol();
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (ctrl)
                    _caretCol = WordLeft(_buffer[_caretLine], _caretCol);
                else if (_caretCol > 0)
                    _caretCol--;
                else if (_caretLine > 0)
                {
                    _caretLine = PrevVisibleLine(_caretLine - 1);
                    _caretCol = LineLength(_caretLine);
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Right:
                ResetPreferredCol();
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (ctrl)
                    _caretCol = WordRight(_buffer[_caretLine], _caretCol);
                else if (_caretCol < LineLength(_caretLine))
                    _caretCol++;
                else if (_caretLine < _buffer.Count - 1)
                {
                    _caretLine = NextVisibleLine(_caretLine + 1);
                    _caretCol = 0;
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Up:
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (_caretLine > 0)
                {
                    if (_preferredCol < 0) _preferredCol = _caretCol;
                    _caretLine = PrevVisibleLine(_caretLine - 1);
                    _caretCol = Math.Min(_preferredCol, LineLength(_caretLine));
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Down:
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (_caretLine < _buffer.Count - 1)
                {
                    if (_preferredCol < 0) _preferredCol = _caretCol;
                    _caretLine = NextVisibleLine(_caretLine + 1);
                    _caretCol = Math.Min(_preferredCol, LineLength(_caretLine));
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Home:
                ResetPreferredCol();
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (ctrl)
                {
                    _caretLine = 0;
                    _caretCol = 0;
                }
                else
                {
                    var text = _buffer[_caretLine];
                    int indent = text.Length - text.TrimStart().Length;
                    _caretCol = _caretCol == indent ? 0 : indent;
                }
                if (!shift) _selection.Clear();
                break;

            case Key.End:
                ResetPreferredCol();
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (ctrl) _caretLine = _buffer.Count - 1;
                _caretCol = LineLength(_caretLine);
                if (!shift) _selection.Clear();
                break;

            case Key.PageUp:
            case Key.PageDown:
                {
                    int visibleLines = Math.Max(1, (int)(_viewport.Height / _font.LineHeight) - 1);
                    if (shift) _selection.Start(_caretLine, _caretCol);
                    if (key == Key.PageUp)
                        _caretLine = PrevVisibleLine(Math.Max(0, _caretLine - visibleLines));
                    else
                        _caretLine = NextVisibleLine(Math.Min(_buffer.Count - 1, _caretLine + visibleLines));
                    _caretCol = Math.Min(_caretCol, LineLength(_caretLine));
                    if (!shift) _selection.Clear();
                    break;
                }
        }
        EnsureCaretVisible();
        ResetCaret();
    }

    private void HandleSelectAll()
    {
        _selection.AnchorLine = 0;
        _selection.AnchorCol = 0;
        _caretLine = _buffer.Count - 1;
        _caretCol = LineLength(_buffer.Count - 1);
        _selection.HasSelection = true;
        InvalidateVisual();
    }

    private void HandleCopy()
    {
        try
        {
            if (_selection.HasSelection)
                Clipboard.SetText(_selection.GetSelectedText(_buffer, _caretLine, _caretCol));
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void HandleCut()
    {
        if (!_selection.HasSelection) return;
        ResetPreferredCol();
        var (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
        var text = _selection.GetSelectedText(_buffer, _caretLine, _caretCol);
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return; // Don't delete if clipboard write failed
        }
        var scope = BeginEdit(sl, el);
        (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
        FinishEdit(scope);
    }

    private void HandlePaste()
    {
        try { if (!Clipboard.ContainsText()) return; }
        catch (System.Runtime.InteropServices.ExternalException) { return; }

        // Read clipboard BEFORE modifying the buffer so a clipboard failure
        // doesn't leave the selection deleted with no undo entry.
        string text;
        try { text = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.ExternalException) { return; }

        ResetPreferredCol();
        var (sl, el) = GetEditRange();
        var scope = BeginEdit(sl, el);
        DeleteSelectionIfPresent();

        // Fast path: no line breaks — skip Split which scans the entire string
        if (!text.AsSpan().ContainsAny('\r', '\n'))
        {
            var expanded = TextBuffer.ExpandTabs(text, TabSize);
            _buffer.InsertAt(_caretLine, _caretCol, expanded);
            _caretCol += expanded.Length;
        }
        else
        {
            var pasteLines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            for (int pi = 0; pi < pasteLines.Length; pi++)
                pasteLines[pi] = TextBuffer.ExpandTabs(pasteLines[pi], TabSize);

            if (pasteLines.Length == 1)
            {
                _buffer.InsertAt(_caretLine, _caretCol, pasteLines[0]);
                _caretCol += pasteLines[0].Length;
            }
            else
            {
                var after = _buffer.TruncateAt(_caretLine, _caretCol);
                _buffer.InsertAt(_caretLine, _caretCol, pasteLines[0]);
                for (int i = 1; i < pasteLines.Length; i++)
                {
                    _caretLine++;
                    _buffer.InsertLine(_caretLine, pasteLines[i]);
                }
                _caretCol = LineLength(_caretLine);
                _buffer.InsertAt(_caretLine, _caretCol, after);
            }
        }
        FinishEdit(scope);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Line manipulation shortcuts
    // ──────────────────────────────────────────────────────────────────

    private void HandleDuplicateLine()
    {
        ResetPreferredCol();
        if (_selection.HasSelection)
        {
            var (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
            int count = el - sl + 1;
            var scope = BeginEdit(sl, el + count);
            TextBuffer.LineSnapshot lines = _buffer.SnapshotLines(sl, count);
            _buffer.InsertLineSnapshot(el + 1, lines);
            _caretLine += count;
            _selection.AnchorLine += count;
            FinishEdit(scope);
        }
        else
        {
            var scope = BeginEdit(_caretLine, _caretLine + 1);
            _buffer.InsertLine(_caretLine + 1, _buffer[_caretLine]);
            _caretLine++;
            FinishEdit(scope);
        }
    }

    private void HandleDeleteLine()
    {
        ResetPreferredCol();
        if (_buffer.Count == 1)
        {
            var scope = BeginEdit(0, 0);
            _buffer.NotifyLineChanging(0);
            _buffer[0] = "";
            _caretLine = 0;
            _caretCol = 0;
            FinishEdit(scope);
            return;
        }

        int sl, el;
        if (_selection.HasSelection)
        {
            (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
        }
        else
        {
            sl = _caretLine;
            el = _caretLine;
        }

        int count = el - sl + 1;
        bool deletingAll = count >= _buffer.Count;
        var editScope = BeginEdit(sl, Math.Min(el + 1, _buffer.Count - 1));
        if (deletingAll)
        {
            _buffer.RemoveRange(1, _buffer.Count - 1);
            _buffer.NotifyLineChanging(0);
            _buffer[0] = "";
            _caretLine = 0;
            _caretCol = 0;
        }
        else
        {
            _buffer.RemoveRange(sl, count);
            _caretLine = Math.Min(sl, _buffer.Count - 1);
            _caretCol = Math.Min(_caretCol, LineLength(_caretLine));
        }
        _selection.Clear();
        FinishEdit(editScope);
    }

    private void HandleMoveLine(bool up)
    {
        ResetPreferredCol();
        int sl, el;
        if (_selection.HasSelection)
        {
            (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
        }
        else
        {
            sl = _caretLine;
            el = _caretLine;
        }

        if (up && sl == 0) return;

        if (!up && el >= _buffer.Count - 1)
        {
            // At the bottom edge, append an empty line then use the normal swap logic
            var scope2 = BeginEdit(sl, el);
            _buffer.InsertLine(el + 1, "");
            // Swap: move the new empty line from el+1 to sl
            _buffer.RemoveRange(el + 1, 1);
            _buffer.InsertLine(sl, "");
            _caretLine++;
            if (_selection.HasSelection)
                _selection.AnchorLine++;
            FinishEdit(scope2);
            return;
        }

        var scope = BeginEdit(up ? sl - 1 : sl, up ? el : el + 1);
        if (up)
        {
            var line = _buffer[sl - 1];
            _buffer.RemoveRange(sl - 1, 1);
            _buffer.InsertLine(el, line);
            _caretLine--;
            if (_selection.HasSelection)
                _selection.AnchorLine--;
        }
        else
        {
            var line = _buffer[el + 1];
            _buffer.RemoveRange(el + 1, 1);
            _buffer.InsertLine(sl, line);
            _caretLine++;
            if (_selection.HasSelection)
                _selection.AnchorLine++;
        }
        FinishEdit(scope);
    }

    private void HandleInsertBlankLine(bool above)
    {
        ResetPreferredCol();
        var scope = BeginEdit(_caretLine, _caretLine);
        int insertAt = above ? _caretLine : _caretLine + 1;
        _buffer.InsertLine(insertAt, "");
        _caretLine = insertAt;
        _caretCol = 0;
        _selection.Clear();
        FinishEdit(scope);
    }

    private void HandleSelectLine()
    {
        _selection.AnchorLine = _caretLine;
        _selection.AnchorCol = 0;
        _selection.HasSelection = true;
        if (_caretLine < _buffer.Count - 1)
        {
            _caretLine++;
            _caretCol = 0;
        }
        else
        {
            _caretCol = LineLength(_caretLine);
        }
        ResetPreferredCol();
        EnsureCaretVisible();
        ResetCaret();
        _textVisualDirty = true;
        InvalidateVisual();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Mouse wheel
    // ──────────────────────────────────────────────────────────────────
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_isBusy)
        {
            e.Handled = true;
            return;
        }

        SetVerticalOffset(_offset.Y - e.Delta / MouseWheelDeltaUnit * _font.LineHeight * ScrollWheelLines);
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────
    //  IScrollInfo
    // ──────────────────────────────────────────────────────────────────
    private const double MinThumbPixels = 30;
    private double ThumbPadding(double extent, double viewport)
    {
        if (extent <= viewport || viewport <= MinThumbPixels) return 0;
        double targetRatio = MinThumbPixels / viewport;
        if (viewport / extent >= targetRatio) return 0;
        return (targetRatio * extent - viewport) / (1 - targetRatio);
    }
    public double ExtentWidth => _extent.Width + ThumbPadding(_extent.Width, _viewport.Width);
    public double ExtentHeight => _extent.Height + ThumbPadding(_extent.Height, _viewport.Height);
    public double ViewportWidth => _viewport.Width + ThumbPadding(_extent.Width, _viewport.Width);
    public double ViewportHeight => _viewport.Height + ThumbPadding(_extent.Height, _viewport.Height);
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public void SetHorizontalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        offset = Math.Round(offset * _font.Dpi) / _font.Dpi;
        if (Math.Abs(offset - _offset.X) < 0.01) return;
        _offset.X = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        offset = Math.Round(offset * _font.Dpi) / _font.Dpi;
        if (Math.Abs(offset - _offset.Y) < 0.01) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    public void LineUp() => SetVerticalOffset(_offset.Y - _font.LineHeight);
    public void LineDown() => SetVerticalOffset(_offset.Y + _font.LineHeight);
    public void LineLeft() => SetHorizontalOffset(_offset.X - _font.CharWidth * TabSize);
    public void LineRight() => SetHorizontalOffset(_offset.X + _font.CharWidth * TabSize);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);
    public void MouseWheelUp() => LineUp();
    public void MouseWheelDown() => LineDown();
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();

    // ──────────────────────────────────────────────────────────────────
    //  Layout
    // ──────────────────────────────────────────────────────────────────
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Math.Abs(availableSize.Width - _viewport.Width) > 0.5
            || Math.Abs(availableSize.Height - _viewport.Height) > 0.5)
        {
            _textVisualDirty = true;
            _gutterVisualDirty = true;
        }
        _viewport = availableSize;
        UpdateExtent();
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        bool viewportChanged = Math.Abs(finalSize.Width - _lastArrangeSize.Width) > 0.01
                            || Math.Abs(finalSize.Height - _lastArrangeSize.Height) > 0.01;
        _lastArrangeSize = finalSize;
        _viewport = finalSize;
        if (viewportChanged)
        {
            _textVisualDirty = true;
            _gutterVisualDirty = true;
            SetHorizontalOffset(_offset.X);
            SetVerticalOffset(_offset.Y);
            UpdateBusyVisual();
            ScrollOwner?.InvalidateScrollInfo();
        }
        return finalSize;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Public API for file operations
    // ──────────────────────────────────────────────────────────────────
    public string LineEnding => _buffer.LineEndingDisplay;

    public void SetContent(string text)
    {
        _buffer.SetContent(text, TabSize);
        ResetAfterContentLoad();
    }

    /// <summary>
    /// Apply pre-parsed content (from <see cref="TextBuffer.PrepareContent"/>).
    /// Use this after offloading the heavy parsing to a background thread.
    /// </summary>
    public void SetPreparedContent(TextBuffer.PreparedContent prepared)
    {
        _buffer.SetPreparedContent(prepared);
        ResetAfterContentLoad();
    }

    private void ResetAfterContentLoad()
    {
        _caretLine = 0;
        _caretCol = 0;
        _selection.Clear();
        _undoManager.Clear();
        _cleanUndoDepth = 0;
        _find.Clear();
        _lineNumStrings.Clear();
        InvalidateLanguageAnalysis();
        SuppressWordWrapIfNeeded();
        UpdateExtent();
        SetVerticalOffset(0);
        SetHorizontalOffset(0);
        InvalidateText();
    }

    public void SetCaretPosition(int line, int col)
    {
        if (_buffer.Count == 0) return;
        _caretLine = Math.Clamp(line, 0, _buffer.Count - 1);
        _caretCol = Math.Clamp(col, 0, LineLength(_caretLine));
        _selection.Clear();
    }

    /// <summary>
    /// Replaces the buffer content while preserving scroll position and caret.
    /// Used when reloading a file that changed on disk.
    /// </summary>
    public void ReloadContent(string text)
    {
        var savedVOffset = _offset.Y;
        var savedHOffset = _offset.X;
        var savedLine = _caretLine;
        var savedCol = _caretCol;

        _buffer.SetContent(text, TabSize);
        _selection.Clear();
        _undoManager.Clear();
        _cleanUndoDepth = 0;
        _find.Clear();
        InvalidateLanguageAnalysis();
        SuppressWordWrapIfNeeded();
        UpdateExtent();

        // Clamp caret to new buffer bounds
        _caretLine = Math.Min(savedLine, _buffer.Count - 1);
        _caretCol = Math.Min(savedCol, LineLength(_caretLine));

        SetVerticalOffset(savedVOffset);
        SetHorizontalOffset(savedHOffset);
        InvalidateText();
    }

    public void ReloadPreparedContent(TextBuffer.PreparedContent prepared)
    {
        var savedVOffset = _offset.Y;
        var savedHOffset = _offset.X;
        var savedLine = _caretLine;
        var savedCol = _caretCol;

        _buffer.SetPreparedContent(prepared);
        _selection.Clear();
        _undoManager.Clear();
        _cleanUndoDepth = 0;
        _find.Clear();
        InvalidateLanguageAnalysis();
        SuppressWordWrapIfNeeded();
        UpdateExtent();

        _caretLine = Math.Min(savedLine, _buffer.Count - 1);
        _caretCol = Math.Min(savedCol, LineLength(_caretLine));

        SetVerticalOffset(savedVOffset);
        SetHorizontalOffset(savedHOffset);
        InvalidateText();
    }

    /// <summary>
    /// Appends new text to the end of the buffer without resetting scroll, caret, or undo.
    /// Used for incremental reload of append-only files (e.g. log files).
    /// </summary>
    public void AppendContent(string text)
    {
        _buffer.AppendContent(text, TabSize);

        InvalidateLanguageAnalysis();

        SuppressWordWrapIfNeeded();
        UpdateExtent();
        InvalidateText();
    }

    public string GetContent() => _buffer.GetContent();

    public sealed record SaveSnapshot(TextBuffer.LineSnapshot Lines, string LineEnding, int TabSize);

    public SaveSnapshot CreateSaveSnapshot() =>
        new(_buffer.SnapshotLines(0, _buffer.Count), _buffer.LineEnding, TabSize);

    public void ApplySavedContent(TextBuffer.PreparedContent prepared)
    {
        _buffer.SetPreparedContent(prepared);
        InvalidateLanguageAnalysis();
        UpdateExtent();
        InvalidateText();
    }

    public void SaveToFile(string path, System.Text.Encoding encoding)
    {
        ApplySavedContent(TextBuffer.SaveSnapshotToFile(path, encoding, TabSize,
            _buffer.SnapshotLines(0, _buffer.Count), _buffer.LineEnding));
    }

    /// <summary>
    /// Release undo history, buffer, and caches to free memory when closing a tab.
    /// Returns true if the released data was large enough to warrant a GC.
    /// </summary>
    public bool ReleaseResources()
    {
        bool large = _buffer.Count > 10_000;
        _undoManager.Clear();
        _buffer.Clear();
        InvalidateLanguageAnalysis();
        // TrimExcess releases the backing arrays that Clear() leaves allocated.
        _find.Clear(trimExcess: true);
        _lineNumStrings.Clear();
        _lineNumStrings.TrimExcess();
        return large;
    }

    public void InvalidateLanguage()
    {
        InvalidateLanguageAnalysis();
        InvalidateText();
    }

    public void MarkClean()
    {
        _cleanUndoDepth = _undoManager.UndoCount;
        _buffer.IsDirty = false;
    }

    public void MarkDirty()
    {
        _cleanUndoDepth = -1;
        _buffer.IsDirty = true;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Find support (delegates to FindManager)
    // ──────────────────────────────────────────────────────────────────

    private int _findNavigationVersion;

    public int FindMatchCount => _find.MatchCount;
    public long FindKnownMatchCount => _find.KnownMatchCount;
    public int CurrentMatchIndex => _find.CurrentIndex;
    public bool HasCurrentFindMatch => _find.GetCurrentMatch() != null;
    public bool IsFindSearching => _find.IsSearching;
    public bool HasExactFindMatchCount => _find.HasExactMatchCount;
    public string FindStatusText => _find.StatusText;

    public string GetSelectedText()
    {
        return _selection.GetSelectedText(_buffer, _caretLine, _caretCol);
    }

    public (int startLine, int startCol, int endLine, int endCol)? GetSelectionBounds()
    {
        if (!_selection.HasSelection) return null;
        var (sl, sc, el, ec) = _selection.GetOrdered(_caretLine, _caretCol);
        return (sl, sc, el, ec);
    }

    public void SetFindMatches(string query, bool matchCase, bool useRegex = false, bool wholeWord = false,
        (int, int, int, int)? selectionBounds = null, bool preserveSelection = false)
    {
        int version = ++_findNavigationVersion;
        int caretLine = _caretLine;
        int caretCol = _caretCol;
        _find.StartSearch(_buffer, query, matchCase, caretLine, caretCol, useRegex, wholeWord,
            selectionBounds);
        _ = NavigateToInitialFindResultAsync(version, caretLine, caretCol, preserveSelection);
        InvalidateVisual();
    }

    public void ClearFindMatches()
    {
        _findNavigationVersion++;
        _find.Clear();
        InvalidateVisual();
    }

    public async void FindNext(bool preserveSelection = false)
    {
        int version = ++_findNavigationVersion;
        if (await _find.MoveNextAsync(_caretLine, _caretCol) && version == _findNavigationVersion)
            NavigateToCurrentMatch(preserveSelection);
        InvalidateVisual();
    }

    public async void FindPrevious(bool preserveSelection = false)
    {
        int version = ++_findNavigationVersion;
        if (await _find.MovePreviousAsync(_caretLine, _caretCol) && version == _findNavigationVersion)
            NavigateToCurrentMatch(preserveSelection);
        InvalidateVisual();
    }

    public void ReplaceCurrent(string replacement)
    {
        var match = _find.GetCurrentMatch();
        if (match == null) return;
        var (line, col, len) = match.Value;
        var scope = BeginEdit(line, line);
        _buffer.ReplaceAt(line, col, len, replacement);
        EndEdit(scope);
        int version = ++_findNavigationVersion;
        _find.StartSearch(_buffer, _find.LastQuery, _find.LastMatchCase, _caretLine, _caretCol,
            _find.LastUseRegex, _find.LastWholeWord, _find.LastSelectionBounds);
        _ = NavigateToInitialFindResultAsync(version, _caretLine, _caretCol, preserveSelection: false);
        _buffer.InvalidateMaxLineLength();
        _gutterVisualDirty = true;
        InvalidateVisual();
    }

    public async void ReplaceAll(string query, string replacement, bool matchCase, bool useRegex = false, bool wholeWord = false,
        (int, int, int, int)? selectionBounds = null)
    {
        SetBusy(true, "Replacing... 0.0%");
        long lastProgressUpdateTicks = 0;
        var progress = new Progress<FindManager.ReplaceAllProgress>(value =>
        {
            long nowTicks = Environment.TickCount64;
            if (!value.IsComplete && nowTicks - lastProgressUpdateTicks < 100)
                return;

            lastProgressUpdateTicks = nowTicks;
            SetBusy(true, FormatReplaceAllProgress(value));
        });
        Exception? replaceError = null;
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            FindManager.ReplaceAllResult? result =
                await _find.CreateReplaceAllSnapshotAsync(replacement, progress).ConfigureAwait(true);
            if (result == null || result.ReplacementCount == 0 || !_find.IsCurrentSession(result.SessionId))
                return;

            var scope = BeginEdit(result.StartLine, result.StartLine + result.LineCount - 1);
            _buffer.ReplaceLines(result.StartLine, result.LineCount, result.Replacement);
            EndEdit(scope);

            ClampCaret();
            _buffer.InvalidateMaxLineLength();
            InvalidateLanguageAnalysis();
            _gutterVisualDirty = true;
            int version = ++_findNavigationVersion;
            _find.StartSearch(_buffer, query, matchCase, _caretLine, _caretCol, useRegex, wholeWord,
                selectionBounds);
            _ = NavigateToInitialFindResultAsync(version, _caretLine, _caretCol, preserveSelection: false);
            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            replaceError = ex;
        }
        finally
        {
            SetBusy(false);
        }

        if (replaceError != null)
            throw new InvalidOperationException("Replace all failed.", replaceError);
    }

    private static string FormatReplaceAllProgress(FindManager.ReplaceAllProgress progress)
    {
        string noun = progress.ReplacementCount == 1 ? "match" : "matches";
        return $"Replacing... {progress.Percent:0.0}% ({progress.ReplacementCount:N0} {noun})";
    }

    private async Task NavigateToInitialFindResultAsync(int version, int caretLine, int caretCol, bool preserveSelection)
    {
        if (string.IsNullOrEmpty(_find.LastQuery))
            return;

        bool found = await _find.FindNearestAsync(caretLine, caretCol);
        if (!found || version != _findNavigationVersion)
            return;

        NavigateToCurrentMatch(preserveSelection);
        InvalidateVisual();
    }

    private void NavigateToCurrentMatch(bool preserveSelection = false)
    {
        var match = _find.GetCurrentMatch();
        if (match == null) return;
        var (line, col, _) = match.Value;

        if (!preserveSelection)
        {
            _caretLine = line;
            _caretCol = col;
            _selection.Clear();
        }

        CentreLineInViewport(line);
        ResetCaret();
    }

    private void CentreLineInViewport(int line)
    {
        double targetY = GetVisualY(line) - (_viewport.Height - _font.LineHeight) / 2;
        SetVerticalOffset(targetY);

        if (_wordWrap)
            return;

        double caretX = _gutterWidth + GutterPadding + _caretCol * _font.CharWidth;
        double textAreaWidth = _viewport.Width - _gutterWidth - GutterPadding;
        double targetX = caretX - _gutterWidth - GutterPadding - textAreaWidth / 2;
        SetHorizontalOffset(targetX);
    }
}
