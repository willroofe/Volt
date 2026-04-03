using System.Linq;

namespace Volt;

/// <summary>Explorer-related actions for the command palette.</summary>
internal record ExplorerActions(
    Action ToggleExplorer,
    Action OpenFolder,
    Action CloseFolder);

/// <summary>Project-related actions for the command palette.</summary>
internal record ProjectActions(
    Action New,
    Action Open,
    Action Save,
    Action Close);

/// <summary>Groups the dependencies needed to build the command palette list.</summary>
internal record CommandPaletteContext(
    List<TabInfo> Tabs,
    AppSettings Settings,
    ThemeManager ThemeManager,
    EditorControl ActiveEditor,
    FindBar FindBar,
    Action SaveSettings,
    ExplorerActions Explorer,
    ProjectActions Project,
    Action ToggleWordWrap);

/// <summary>
/// Builds the command list for the command palette, keeping the 90 lines of
/// preview/commit/revert lambdas out of MainWindow.
/// </summary>
internal static class CommandPaletteCommands
{
    public static List<PaletteCommand> Build(CommandPaletteContext ctx)
    {
        var tabs = ctx.Tabs;
        var settings = ctx.Settings;
        var themeManager = ctx.ThemeManager;
        var activeEditor = ctx.ActiveEditor;
        var findBar = ctx.FindBar;
        var saveSettings = ctx.SaveSettings;
        var explorer = ctx.Explorer;
        var project = ctx.Project;
        var toggleWordWrap = ctx.ToggleWordWrap;
        return
        [
            new("Change Theme", CurrentValue: () => settings.Application.ColorTheme, GetOptions: () =>
            {
                var original = settings.Application.ColorTheme;
                return themeManager.GetAvailableThemes().Select(name => new PaletteOption(
                    name,
                    ApplyPreview: () => themeManager.Apply(name),
                    Commit: () => { settings.Application.ColorTheme = name; saveSettings(); },
                    Revert: () => themeManager.Apply(original)
                )).ToList();
            }),

            new("Change Font Size", CurrentValue: () => activeEditor.EditorFontSize.ToString(), GetOptions: () =>
            {
                var original = activeEditor.EditorFontSize;
                return AppSettings.FontSizeOptions.Select(size => new PaletteOption(
                    size.ToString(),
                    ApplyPreview: () => { foreach (var t in tabs) t.Editor.EditorFontSize = size; },
                    Commit: () => { settings.Editor.Font.Size = size; saveSettings(); },
                    Revert: () => { foreach (var t in tabs) t.Editor.EditorFontSize = original; }
                )).ToList();
            }),

            new("Change Font Family", CurrentValue: () => activeEditor.FontFamilyName, GetOptions: () =>
            {
                var original = activeEditor.FontFamilyName;
                return FontManager.GetMonospaceFonts().Select(name => new PaletteOption(
                    name,
                    ApplyPreview: () => { foreach (var t in tabs) t.Editor.FontFamilyName = name; },
                    Commit: () => { settings.Editor.Font.Family = name; saveSettings(); },
                    Revert: () => { foreach (var t in tabs) t.Editor.FontFamilyName = original; }
                )).ToList();
            }),

            new("Change Font Weight", CurrentValue: () => activeEditor.EditorFontWeight, GetOptions: () =>
            {
                var original = activeEditor.EditorFontWeight;
                return AppSettings.FontWeightOptions.Select(w => new PaletteOption(
                    w,
                    ApplyPreview: () => { foreach (var t in tabs) t.Editor.EditorFontWeight = w; },
                    Commit: () => { settings.Editor.Font.Weight = w; saveSettings(); },
                    Revert: () => { foreach (var t in tabs) t.Editor.EditorFontWeight = original; }
                )).ToList();
            }),

            new("Change Line Height", CurrentValue: () => activeEditor.LineHeightMultiplier.ToString("0.0") + "x", GetOptions: () =>
            {
                var original = activeEditor.LineHeightMultiplier;
                return AppSettings.LineHeightOptions.Select(lh => new PaletteOption(
                    lh.ToString("0.0") + "x",
                    ApplyPreview: () => { foreach (var t in tabs) t.Editor.LineHeightMultiplier = lh; },
                    Commit: () => { settings.Editor.Font.LineHeight = lh; saveSettings(); },
                    Revert: () => { foreach (var t in tabs) t.Editor.LineHeightMultiplier = original; }
                )).ToList();
            }),

            new("Change Tab Size", CurrentValue: () => activeEditor.TabSize.ToString(), GetOptions: () =>
            {
                var original = activeEditor.TabSize;
                return AppSettings.TabSizeOptions.Select(size => new PaletteOption(
                    size.ToString(),
                    ApplyPreview: () => { foreach (var t in tabs) { t.Editor.TabSize = size; t.Editor.InvalidateVisual(); } },
                    Commit: () => { settings.Editor.TabSize = size; saveSettings(); },
                    Revert: () => { foreach (var t in tabs) { t.Editor.TabSize = original; t.Editor.InvalidateVisual(); } }
                )).ToList();
            }),

            new("Toggle Block Caret", Action: () =>
            {
                settings.Editor.Caret.BlockCaret = !settings.Editor.Caret.BlockCaret;
                foreach (var t in tabs)
                {
                    t.Editor.BlockCaret = settings.Editor.Caret.BlockCaret;
                    t.Editor.InvalidateVisual();
                }
                saveSettings();
            }),

            new("Toggle Word Wrap", Action: toggleWordWrap),

            new("Find Bar Position", CurrentValue: () => settings.Editor.Find.BarPosition, GetOptions: () =>
            {
                var original = settings.Editor.Find.BarPosition;
                return AppSettings.FindBarPositionOptions.Select(pos => new PaletteOption(
                    pos,
                    ApplyPreview: () => findBar.SetPosition(pos),
                    Commit: () => { settings.Editor.Find.BarPosition = pos; saveSettings(); },
                    Revert: () => findBar.SetPosition(original)
                )).ToList();
            }),

            new("Toggle File Explorer", Action: explorer.ToggleExplorer),

            new("Explorer: Open Folder...", Action: explorer.OpenFolder),

            new("Explorer: Close Folder", Action: explorer.CloseFolder),

            new("Project: New Project", Action: project.New),

            new("Project: Open Project...", Action: project.Open),

            new("Project: Save Project", Action: project.Save),

            new("Project: Close Project", Action: project.Close),
        ];
    }
}
