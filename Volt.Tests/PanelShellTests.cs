using Xunit;
using System.Windows;

namespace Volt.Tests;

public class PanelShellTests
{
    private sealed class FakePanel(string id, string title) : IPanel
    {
        public string PanelId => id;
        public string Title => title;
        public UIElement Content { get; } = new System.Windows.Controls.Border();
        public event Action? TitleChanged;
    }

    [StaFact]
    public void RegisterPanel_ThenShow_MakesPanelVisible()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test Panel");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        Assert.False(shell.IsPanelVisible("test"));

        shell.ShowPanel("test");
        Assert.True(shell.IsPanelVisible("test"));
    }

    [StaFact]
    public void HidePanel_MakesPanelNotVisible()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test Panel");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);
        shell.ShowPanel("test");

        shell.HidePanel("test");
        Assert.False(shell.IsPanelVisible("test"));
    }

    [StaFact]
    public void TogglePanel_FlipsVisibility()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test Panel");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        shell.TogglePanel("test");
        Assert.True(shell.IsPanelVisible("test"));

        shell.TogglePanel("test");
        Assert.False(shell.IsPanelVisible("test"));
    }

    [StaFact]
    public void MovePanel_ChangesPlacement()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test Panel");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);
        shell.ShowPanel("test");

        shell.MovePanel("test", PanelPlacement.Right);

        var layout = shell.GetCurrentLayout();
        Assert.Single(layout);
        Assert.Equal(PanelPlacement.Right, layout[0].Placement);
        Assert.True(layout[0].Visible);
    }

    [StaFact]
    public void GetCurrentLayout_ReturnsAllRegisteredPanels()
    {
        var shell = new PanelShell();
        var panel1 = new FakePanel("left", "Left");
        var panel2 = new FakePanel("right", "Right");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 200);
        shell.RegisterPanel(panel2, PanelPlacement.Right, 300);
        shell.ShowPanel("left");

        var layout = shell.GetCurrentLayout();
        Assert.Equal(2, layout.Count);

        var leftConfig = layout.First(c => c.PanelId == "left");
        Assert.Equal(PanelPlacement.Left, leftConfig.Placement);
        Assert.Equal(200, leftConfig.Size);
        Assert.True(leftConfig.Visible);

        var rightConfig = layout.First(c => c.PanelId == "right");
        Assert.Equal(PanelPlacement.Right, rightConfig.Placement);
        Assert.Equal(300, rightConfig.Size);
        Assert.False(rightConfig.Visible);
    }

    [StaFact]
    public void RestoreLayout_AppliesConfigToRegisteredPanels()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        shell.RestoreLayout([
            new PanelSlotConfig
            {
                PanelId = "test",
                Placement = PanelPlacement.Right,
                Size = 300,
                Visible = true
            }
        ]);

        var layout = shell.GetCurrentLayout();
        var config = layout.Single();
        Assert.Equal(PanelPlacement.Right, config.Placement);
        Assert.Equal(300, config.Size);
        Assert.True(config.Visible);
    }

    [StaFact]
    public void PanelLayoutChanged_FiresOnShowHide()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        var events = new List<(string id, PanelPlacement placement, double size)>();
        shell.PanelLayoutChanged += (id, p, s) => events.Add((id, p, s));

        shell.ShowPanel("test");
        shell.HidePanel("test");

        Assert.Equal(2, events.Count);
        Assert.Equal("test", events[0].id);
        Assert.Equal("test", events[1].id);
    }

    [StaFact]
    public void ShowPanel_UnknownId_DoesNotThrow()
    {
        var shell = new PanelShell();
        shell.ShowPanel("nonexistent");
    }

    [StaFact]
    public void ShowPanel_AlreadyVisible_DoesNotFireEvent()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);
        shell.ShowPanel("test");

        var events = new List<string>();
        shell.PanelLayoutChanged += (id, _, _) => events.Add(id);

        shell.ShowPanel("test");
        Assert.Empty(events);
    }
}
