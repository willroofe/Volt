using System.Windows;
using System.Windows.Controls;

namespace Volt;

/// <summary>
/// Thin wrapper around an IPanel. Holds the panel reference and hosts its Content.
/// The tab strip and drag handling are managed by TabRegion.
/// </summary>
public class PanelContainer : ContentControl
{
    private readonly IPanel _panel;

    public PanelContainer(IPanel panel)
    {
        _panel = panel;
        Content = panel.Content;
    }

    public string PanelId => _panel.PanelId;
    public IPanel Panel => _panel;
}
