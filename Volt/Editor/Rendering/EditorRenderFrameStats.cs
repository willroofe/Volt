namespace Volt;

internal readonly record struct EditorRenderFrameStats(
    int VisibleLines,
    int DrawnLines,
    int StaticLayerRebuilds,
    int DynamicLayerRedraws,
    int GlyphRuns,
    int SelectionRectangles,
    double PresentMilliseconds);
