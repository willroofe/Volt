using Volt;
using Xunit;

namespace Volt.Tests.Terminal;

public class TerminalShellCatalogTests
{
    private const int PowerShell = (int)TerminalShellPreference.PowerShell;
    private const int CommandPrompt = (int)TerminalShellPreference.CommandPrompt;

    [Theory]
    [InlineData(null, PowerShell)]
    [InlineData("", PowerShell)]
    [InlineData(" ", PowerShell)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", PowerShell)]
    [InlineData("powershell.exe", PowerShell)]
    [InlineData("cmd.exe", CommandPrompt)]
    [InlineData(@"C:\Windows\System32\cmd.exe", CommandPrompt)]
    [InlineData(@"D:\tools\my-shell.exe", PowerShell)]
    public void ClassifyPath_MapsSavedShellPaths(string? path, int expected)
    {
        Assert.Equal((TerminalShellPreference)expected, TerminalShellCatalog.ClassifyPath(path));
    }

    [Theory]
    [InlineData(null, "Shell")]
    [InlineData("", "Shell")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", "PowerShell")]
    [InlineData("pwsh.exe", "PowerShell")]
    [InlineData("powershell.exe", "PowerShell")]
    [InlineData("cmd.exe", "Command Prompt")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "Command Prompt")]
    [InlineData(@"D:\tools\my-shell.exe", "my-shell")]
    public void GetTabTitle_MapsShellPaths(string? path, string expected)
    {
        Assert.Equal(expected, TerminalShellCatalog.GetTabTitle(path));
    }

    [Fact]
    public void ResolveShellPath_UsesFirstAvailablePowerShellCandidate()
    {
        string result = TerminalShellCatalog.ResolveShellPath(
            TerminalShellPreference.PowerShell,
            name => name == "pwsh.exe" ? @"C:\tools\pwsh.exe" : null);

        Assert.Equal(@"C:\tools\pwsh.exe", result);
    }

    [Fact]
    public void ResolveShellPath_FallsBackToWindowsPowerShellCandidate()
    {
        string result = TerminalShellCatalog.ResolveShellPath(
            TerminalShellPreference.PowerShell,
            name => name == "powershell.exe" ? @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" : null);

        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", result);
    }

    [Fact]
    public void ResolveShellPath_UsesPowerShellFallbackWhenNoCandidateExists()
    {
        string result = TerminalShellCatalog.ResolveShellPath(
            TerminalShellPreference.PowerShell,
            _ => null);

        Assert.Equal("powershell.exe", result);
    }

    [Fact]
    public void ResolveShellPath_UsesCommandPromptCandidate()
    {
        string result = TerminalShellCatalog.ResolveShellPath(
            TerminalShellPreference.CommandPrompt,
            name => name == "cmd.exe" ? @"C:\Windows\System32\cmd.exe" : null);

        Assert.Equal(@"C:\Windows\System32\cmd.exe", result);
    }

    [Fact]
    public void ResolveDefaultShell_UsesCatalogCandidateOrder()
    {
        string result = TerminalShellCatalog.ResolveDefaultShell(
            name => name switch
            {
                "powershell.exe" => @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                "cmd.exe" => @"C:\Windows\System32\cmd.exe",
                _ => null
            });

        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", result);
    }

    [Fact]
    public void ResolveDefaultShell_FallsBackToCommandPrompt()
    {
        string result = TerminalShellCatalog.ResolveDefaultShell(_ => null);

        Assert.Equal("cmd.exe", result);
    }
}
