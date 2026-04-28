using Volt;
using Xunit;

namespace Volt.Tests;

public class EditorRendererSettingsTests
{
    [Theory]
    [InlineData(null, "Wpf")]
    [InlineData("", "Wpf")]
    [InlineData("0", "Wpf")]
    [InlineData("false", "Wpf")]
    [InlineData("1", "Direct2D")]
    [InlineData("true", "Direct2D")]
    [InlineData("YES", "Direct2D")]
    public void RequestedMode_ParsesGpuFlag(string? value, string expected)
    {
        Assert.Equal(expected, EditorRendererSettings.RequestedMode(value).ToString());
    }

    [Fact]
    public void RequestedMode_DefaultsToWpf_WhenVariableMissing()
    {
        var env = new Dictionary<string, string?>();

        Assert.Equal("Wpf", EditorRendererSettings.RequestedMode(env).ToString());
    }
}
