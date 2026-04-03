namespace Volt;

public class PanelSlotConfig
{
    public string PanelId { get; set; } = "";
    public PanelPlacement Placement { get; set; } = PanelPlacement.Left;
    public double Size { get; set; } = 250;
    public bool Visible { get; set; }
}
