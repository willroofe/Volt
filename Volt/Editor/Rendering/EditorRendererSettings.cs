namespace Volt;

internal static class EditorRendererSettings
{
    public const string GpuEnvironmentVariable = "VOLT_EDITOR_GPU";

    public static EditorRenderMode RequestedMode() =>
        RequestedMode(Environment.GetEnvironmentVariable(GpuEnvironmentVariable));

    internal static EditorRenderMode RequestedMode(IReadOnlyDictionary<string, string?> environment)
    {
        environment.TryGetValue(GpuEnvironmentVariable, out var value);
        return RequestedMode(value);
    }

    internal static EditorRenderMode RequestedMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return EditorRenderMode.Wpf;
        return value.Trim() is "1" or "true" or "TRUE" or "True" or "yes" or "YES" or "Yes"
            ? EditorRenderMode.Direct2D
            : EditorRenderMode.Wpf;
    }
}
