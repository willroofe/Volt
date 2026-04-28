using System.Windows.Media;

namespace Volt;

internal interface IEditorRenderer : IDisposable
{
    EditorRenderMode Mode { get; }
    bool IsAvailable { get; }
    string? FallbackReason { get; }
    ImageSource? ImageSource { get; }
}
