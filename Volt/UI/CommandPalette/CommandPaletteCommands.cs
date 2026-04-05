using System.Linq;

namespace Volt;

/// <summary>Explorer-related actions for the command palette.</summary>
internal record ExplorerActions(
    Action ToggleExplorer,
    Action OpenFolder,
    Action CloseFolder);

/// <summary>Workspace-related actions for the command palette.</summary>
internal record WorkspaceActions(
    Action New,
    Action Open,
    Action Close,
    Action AddFolder);

/// <summary>Groups the dependencies needed to build the command palette list.</summary>
internal record CommandPaletteContext(
    List<TabInfo> Tabs,
    AppSettings Settings,
    ThemeManager ThemeManager,
    EditorControl ActiveEditor,
    FindBar FindBar,
    CommandPalette CommandPalette,
    Action SaveSettings,
    ExplorerActions Explorer,
    WorkspaceActions Workspace,
    Action ToggleWordWrap,
    Action ToggleWordWrapAtWords,
    Action ToggleWordWrapIndent,
    Action ToggleFixedWidthTabs);

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
        var cmdPalette = ctx.CommandPalette;
        var saveSettings = ctx.SaveSettings;
        var explorer = ctx.Explorer;
        var workspace = ctx.Workspace;
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

            new("Toggle Word Wrap at Word Boundaries", Action: ctx.ToggleWordWrapAtWords),

            new("Toggle Word Wrap Indent", Action: ctx.ToggleWordWrapIndent),

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

            new("Command Palette Position", CurrentValue: () => settings.Application.CommandPalettePosition, GetOptions: () =>
            {
                var original = settings.Application.CommandPalettePosition;
                return AppSettings.CommandPalettePositionOptions.Select(pos => new PaletteOption(
                    pos,
                    ApplyPreview: () => cmdPalette.SetPosition(pos),
                    Commit: () => { settings.Application.CommandPalettePosition = pos; saveSettings(); },
                    Revert: () => cmdPalette.SetPosition(original)
                )).ToList();
            }),

            new("Toggle Fixed Width Tabs", Action: ctx.ToggleFixedWidthTabs),

            new("Toggle File Explorer", Action: explorer.ToggleExplorer),

            new("Explorer: Open Folder", Action: explorer.OpenFolder),

            new("Explorer: Close Folder", Action: explorer.CloseFolder),

            new("Workspace: New Workspace", Action: workspace.New),

            new("Workspace: Open Workspace", Action: workspace.Open),

            new("Workspace: Close Workspace", Action: workspace.Close),

            new("Workspace: Add Folder to Workspace", Action: workspace.AddFolder),
        ];
    }
}
