using System.IO;

namespace Volt;

internal enum TerminalShellPreference
{
    PowerShell,
    CommandPrompt,
}

internal readonly record struct TerminalShellOption(
    TerminalShellPreference Preference,
    string DisplayName,
    string[] ExecutableCandidates,
    string FallbackExecutable);

internal static class TerminalShellCatalog
{
    public static IReadOnlyList<TerminalShellOption> Options { get; } =
    [
        new(
            TerminalShellPreference.PowerShell,
            "PowerShell",
            ["pwsh.exe", "powershell.exe"],
            "powershell.exe"),
        new(
            TerminalShellPreference.CommandPrompt,
            "Command Prompt",
            ["cmd.exe"],
            "cmd.exe"),
    ];

    public static TerminalShellPreference PreferenceAt(int index)
        => index >= 0 && index < Options.Count ? Options[index].Preference : Options[0].Preference;

    public static int IndexOf(TerminalShellPreference preference)
    {
        for (int i = 0; i < Options.Count; i++)
        {
            if (Options[i].Preference == preference)
                return i;
        }

        return 0;
    }

    public static TerminalShellPreference ClassifyPath(string? shellPath)
    {
        if (string.IsNullOrWhiteSpace(shellPath))
            return TerminalShellPreference.PowerShell;

        return TryGetOptionForExecutable(GetExecutableName(shellPath), out var option)
            ? option.Preference
            : TerminalShellPreference.PowerShell;
    }

    public static string ResolveShellPath(
        TerminalShellPreference preference,
        Func<string, string?>? executableResolver = null)
    {
        var option = GetOption(preference);
        var resolver = executableResolver ?? FindInPath;

        foreach (string candidate in option.ExecutableCandidates)
        {
            if (resolver(candidate) is { } path)
                return path;
        }

        return option.FallbackExecutable;
    }

    public static string ResolveDefaultShell(Func<string, string?>? executableResolver = null)
    {
        var resolver = executableResolver ?? FindInPath;

        foreach (var option in Options)
        {
            foreach (string candidate in option.ExecutableCandidates)
            {
                if (resolver(candidate) is { } path)
                    return path;
            }
        }

        return GetOption(TerminalShellPreference.CommandPrompt).FallbackExecutable;
    }

    public static string GetTabTitle(string? shellPath)
    {
        if (string.IsNullOrWhiteSpace(shellPath))
            return "Shell";

        string file = GetExecutableName(shellPath);
        if (TryGetOptionForExecutable(file, out var option))
            return option.DisplayName;

        return Path.GetFileNameWithoutExtension(file);
    }

    private static TerminalShellOption GetOption(TerminalShellPreference preference)
    {
        foreach (var option in Options)
        {
            if (option.Preference == preference)
                return option;
        }

        return Options[0];
    }

    private static bool TryGetOptionForExecutable(string executableName, out TerminalShellOption option)
    {
        foreach (var candidateOption in Options)
        {
            foreach (string candidate in candidateOption.ExecutableCandidates)
            {
                if (string.Equals(executableName, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    option = candidateOption;
                    return true;
                }
            }
        }

        option = default;
        return false;
    }

    private static string GetExecutableName(string shellPath)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(shellPath));

    private static string? FindInPath(string executableName)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (string path in paths)
        {
            try
            {
                string candidate = Path.Combine(path, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
            }
        }

        return null;
    }
}
