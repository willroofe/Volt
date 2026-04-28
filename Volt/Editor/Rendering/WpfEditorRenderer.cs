using System.Windows.Media;

namespace Volt;

internal sealed class WpfEditorRenderer : IEditorRenderer
{
    public EditorRenderMode Mode => EditorRenderMode.Wpf;
    public bool IsAvailable => true;
    public string? FallbackReason => null;
    public ImageSource? ImageSource => null;
    public void Dispose() { }
}
