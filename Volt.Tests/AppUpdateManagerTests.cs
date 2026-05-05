using Volt;
using Xunit;

namespace Volt.Tests;

public class AppUpdateManagerTests
{
    [Fact]
    public void GetReleasePageUrl_AddsExpectedTagPrefix()
    {
        string url = AppUpdateManager.GetReleasePageUrl("1.3.2");

        Assert.Equal("https://github.com/willroofe/Volt/releases/tag/v1.3.2", url);
    }

    [Fact]
    public void GetReleasePageUrl_PreservesExistingTagPrefix()
    {
        string url = AppUpdateManager.GetReleasePageUrl("v1.3.2");

        Assert.Equal("https://github.com/willroofe/Volt/releases/tag/v1.3.2", url);
    }
}
