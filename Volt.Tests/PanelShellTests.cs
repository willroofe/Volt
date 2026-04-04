using Xunit;
using System.Windows;

namespace Volt.Tests;

public class PanelShellTests
{
    private sealed class FakePanel(string id, string title) : IPanel
    {
        public string PanelId => id;
        public string Title => title;
        public string? IconGlyph => null;
        public UIElement Content { get; } = new System.Windows.Controls.Border();
#pragma warning disable CS0067
        public event Action? TitleChanged;
#pragma warning restore CS0067
    }

    private static FakePanel CreatePanel(string id = "test", string title = "Test Panel") => new(id, title);

    [StaFact]
    public void RegisterPanel_ThenShow_MakesPanelVisible()
    {
        var shell = new PanelShell();
        var panel = CreatePanel();
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        Assert.False(shell.IsPanelVisible("test"));

        shell.ShowPanel("test");
        Assert.True(shell.IsPanelVisible("test"));
    }

    [StaFact]
    public void HidePanel_MakesPanelNotVisible()
    {
        var shell = new PanelShell();
        var panel = CreatePanel();
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);
        shell.ShowPanel("test");

        shell.HidePanel("test");
        Assert.False(shell.IsPanelVisible("test"));
    }

    [StaFact]
    public void TogglePanel_FlipsVisibility()
    {
        var shell = new PanelShell();
        var panel = CreatePanel();
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
        var panel = CreatePanel();
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
        var panel1 = CreatePanel("left", "Left");
        var panel2 = CreatePanel("right", "Right");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 200);
        shell.RegisterPanel(panel2, PanelPlacement.Right, 300);
        shell.ShowPanel("left");

        var layout = shell.GetCurrentLayout();
        Assert.Equal(2, layout.Count);

        var leftConfig = layout.First(c => c.PanelId == "left");
        Assert.Equal(PanelPlacement.Left, leftConfig.Placement);
        Assert.True(leftConfig.Visible);
        Assert.True(leftConfig.IsActiveTab);

        var rightConfig = layout.First(c => c.PanelId == "right");
        Assert.Equal(PanelPlacement.Right, rightConfig.Placement);
        Assert.False(rightConfig.Visible);
    }

    [StaFact]
    public void RestoreLayout_AppliesConfigToRegisteredPanels()
    {
        var shell = new PanelShell();
        var panel = CreatePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        shell.RestoreLayout([
            new PanelSlotConfig
            {
                PanelId = "test",
                Placement = PanelPlacement.Right,
                Size = 300,
                Visible = true,
                TabIndex = 0,
                IsActiveTab = true
            }
        ]);

        var layout = shell.GetCurrentLayout();
        var config = layout.Single();
        Assert.Equal(PanelPlacement.Right, config.Placement);
        Assert.True(config.Visible);
        Assert.True(config.IsActiveTab);
    }

    [StaFact]
    public void PanelLayoutChanged_FiresOnShowHide()
    {
        var shell = new PanelShell();
        var panel = CreatePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        var events = new List<(string id, PanelPlacement placement, double size)>();
        shell.PanelLayoutChanged += (id, p, s) => events.Add((id, p, s));

        shell.ShowPanel("test");
        shell.HidePanel("test");

        // ShowPanel fires 2 events (show + active tab change), HidePanel fires 1
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal("test", e.id));
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
        var panel = CreatePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);
        shell.ShowPanel("test");

        var events = new List<string>();
        shell.PanelLayoutChanged += (id, _, _) => events.Add(id);

        shell.ShowPanel("test");
        Assert.Empty(events);
    }

    [StaFact]
    public void MultiplePanels_SameRegion_BothVisible()
    {
        var shell = new PanelShell();
        var panel1 = CreatePanel("a", "Panel A");
        var panel2 = CreatePanel("b", "Panel B");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 250);
        shell.RegisterPanel(panel2, PanelPlacement.Left, 250);

        shell.ShowPanel("a");
        shell.ShowPanel("b");

        Assert.True(shell.IsPanelVisible("a"));
        Assert.True(shell.IsPanelVisible("b"));

        var layout = shell.GetCurrentLayout();
        var configA = layout.First(c => c.PanelId == "a");
        var configB = layout.First(c => c.PanelId == "b");
        Assert.Equal(0, configA.TabIndex);
        Assert.Equal(1, configB.TabIndex);
        // Last shown panel is active
        Assert.True(configB.IsActiveTab);
    }

    [StaFact]
    public void HidePanel_InMultiTabRegion_DoesNotCollapseRegion()
    {
        var shell = new PanelShell();
        var panel1 = CreatePanel("a", "Panel A");
        var panel2 = CreatePanel("b", "Panel B");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 250);
        shell.RegisterPanel(panel2, PanelPlacement.Left, 250);

        shell.ShowPanel("a");
        shell.ShowPanel("b");

        shell.HidePanel("a");

        Assert.False(shell.IsPanelVisible("a"));
        Assert.True(shell.IsPanelVisible("b"));
    }

    [StaFact]
    public void GetAvailablePanels_ReturnsHiddenPanels()
    {
        var shell = new PanelShell();
        var panel1 = CreatePanel("a", "Panel A");
        var panel2 = CreatePanel("b", "Panel B");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 250);
        shell.RegisterPanel(panel2, PanelPlacement.Right, 250);

        shell.ShowPanel("a");

        var available = shell.GetAvailablePanels();
        Assert.Single(available);
        Assert.Equal("b", available[0].PanelId);
    }

    [StaFact]
    public void ToggleRegion_CollapsesAndRestores()
    {
        var shell = new PanelShell();
        var panel = CreatePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);
        shell.ShowPanel("test");

        // Collapse
        shell.ToggleRegion(PanelPlacement.Left);
        Assert.False(shell.IsPanelVisible("test"));

        // Restore
        shell.ToggleRegion(PanelPlacement.Left);
        Assert.True(shell.IsPanelVisible("test"));
    }

    [StaFact]
    public void RestoreLayout_PreservesTabOrder()
    {
        var shell = new PanelShell();
        var panel1 = CreatePanel("a", "Panel A");
        var panel2 = CreatePanel("b", "Panel B");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 250);
        shell.RegisterPanel(panel2, PanelPlacement.Left, 250);

        shell.RestoreLayout([
            new PanelSlotConfig { PanelId = "b", Placement = PanelPlacement.Left, Size = 250, Visible = true, TabIndex = 0, IsActiveTab = false },
            new PanelSlotConfig { PanelId = "a", Placement = PanelPlacement.Left, Size = 250, Visible = true, TabIndex = 1, IsActiveTab = true }
        ]);

        var layout = shell.GetCurrentLayout();
        var configA = layout.First(c => c.PanelId == "a");
        var configB = layout.First(c => c.PanelId == "b");
        Assert.Equal(1, configA.TabIndex);
        Assert.Equal(0, configB.TabIndex);
        Assert.True(configA.IsActiveTab);
    }
}
