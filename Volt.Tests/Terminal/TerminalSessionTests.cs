using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class TerminalSessionTests
{
    [Theory]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", "PowerShell")]
    [InlineData("pwsh.exe", "PowerShell")]
    [InlineData("powershell.exe", "PowerShell")]
    [InlineData("cmd.exe", "Command Prompt")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "Command Prompt")]
    [InlineData(@"D:\tools\my-shell.exe", "my-shell")]
    public void ShellTabLabel_MapsKnownShells(string path, string expected)
    {
        Assert.Equal(expected, TerminalSession.ShellTabLabel(path));
    }
}
