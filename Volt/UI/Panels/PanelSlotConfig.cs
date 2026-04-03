namespace Volt;

public class PanelSlotConfig
{
    public string PanelId { get; set; } = "";
    public PanelPlacement Placement { get; set; } = PanelPlacement.Left;
    public double Size { get; set; } = 250;
    public bool Visible { get; set; }
    public int TabIndex { get; set; }
    public bool IsActiveTab { get; set; }
}
