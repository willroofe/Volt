using System;
using Volt;
using Xunit;

namespace Volt.Tests;

public class EditorRendererSettingsTests
{
    [Fact]
    public void PreferredMode_DefaultsToDirect2D()
    {
        Assert.Equal("Direct2D", EditorRendererSettings.PreferredMode().ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("1")]
    public void PreferredMode_IgnoresLegacyGpuEnvironmentFlag(string? value)
    {
        string? previous = Environment.GetEnvironmentVariable("VOLT_EDITOR_GPU");
        try
        {
            Environment.SetEnvironmentVariable("VOLT_EDITOR_GPU", value);

            Assert.Equal("Direct2D", EditorRendererSettings.PreferredMode().ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOLT_EDITOR_GPU", previous);
        }
    }
}
