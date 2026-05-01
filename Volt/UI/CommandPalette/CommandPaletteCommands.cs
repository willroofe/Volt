using System.Linq;

namespace Volt;

/// <summary>Explorer-related actions for the command palette.</summary>
internal record ExplorerActions(
    Action ToggleExplorer,
    Action OpenFolder,
    Action CloseFolder);

/// <summary>Workspace-related actions for the command palette.</summary>
internal record WorkspaceActions(
    Action Open,
    Action Close,
    Action AddFolder,
    Action SaveAs);

/// <summary>Terminal-related actions for the command palette.</summary>
internal record TerminalActions(
    Action Toggle,
    Action NewSession);

/// <summary>Editor split / join actions for the command palette.</summary>
internal record EditorSplitActions(
    Action ToggleSplit,
    Action JoinWithSibling,
    Action JoinAll,
    Action SwitchOrientation,
    Action FocusNextLeaf);

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
    Action ToggleFixedWidthTabs,
    Action CheckForUpdates,
    Action OpenRecent,
    Action<string?> SetLanguage,
    TerminalActions Terminal,
    Action SyncTerminalFromActiveEditor,
    EditorSplitActions EditorSplit);

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
        var syntaxManager = App.Current.SyntaxManager;
        var explorer = ctx.Explorer;
        var workspace = ctx.Workspace;
        var toggleWordWrap = ctx.ToggleWordWrap;
        void syncTerm() => ctx.SyncTerminalFromActiveEditor();
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
                    ApplyPreview: () =>
                    {
                        foreach (var t in tabs) t.Editor.EditorFontSize = size;
                        syncTerm();
                    },
                    Commit: () => { settings.Editor.Font.Size = size; saveSettings(); syncTerm(); },
                    Revert: () =>
                    {
                        foreach (var t in tabs) t.Editor.EditorFontSize = original;
                        syncTerm();
                    }
                )).ToList();
            }),

            new("Change Font Family", CurrentValue: () => activeEditor.FontFamilyName, GetOptions: () =>
            {
                var original = activeEditor.FontFamilyName;
                return FontManager.GetMonospaceFonts().Select(name => new PaletteOption(
                    name,
                    ApplyPreview: () =>
                    {
                        foreach (var t in tabs) t.Editor.FontFamilyName = name;
                        syncTerm();
                    },
                    Commit: () => { settings.Editor.Font.Family = name; saveSettings(); syncTerm(); },
                    Revert: () =>
                    {
                        foreach (var t in tabs) t.Editor.FontFamilyName = original;
                        syncTerm();
                    }
                )).ToList();
            }),

            new("Change Font Weight", CurrentValue: () => activeEditor.EditorFontWeight, GetOptions: () =>
            {
                var original = activeEditor.EditorFontWeight;
                return AppSettings.FontWeightOptions.Select(w => new PaletteOption(
                    w,
                    ApplyPreview: () =>
                    {
                        foreach (var t in tabs) t.Editor.EditorFontWeight = w;
                        syncTerm();
                    },
                    Commit: () => { settings.Editor.Font.Weight = w; saveSettings(); syncTerm(); },
                    Revert: () =>
                    {
                        foreach (var t in tabs) t.Editor.EditorFontWeight = original;
                        syncTerm();
                    }
                )).ToList();
            }),

            new("Change Line Height", CurrentValue: () => activeEditor.LineHeightMultiplier.ToString("0.0") + "x", GetOptions: () =>
            {
                var original = activeEditor.LineHeightMultiplier;
                return AppSettings.LineHeightOptions.Select(lh => new PaletteOption(
                    lh.ToString("0.0") + "x",
                    ApplyPreview: () =>
                    {
                        foreach (var t in tabs) t.Editor.LineHeightMultiplier = lh;
                        syncTerm();
                    },
                    Commit: () => { settings.Editor.Font.LineHeight = lh; saveSettings(); syncTerm(); },
                    Revert: () =>
                    {
                        foreach (var t in tabs) t.Editor.LineHeightMultiplier = original;
                        syncTerm();
                    }
                )).ToList();
            }),

            new("Change Tab Size", CurrentValue: () => activeEditor.TabSize.ToString(), GetOptions: () =>
            {
                var original = activeEditor.TabSize;
                return AppSettings.TabSizeOptions.Select(size => new PaletteOption(
                    size.ToString(),
                    ApplyPreview: () => { foreach (var t in tabs) { t.Editor.TabSize = size; t.Editor.InvalidateEditorVisual(); } },
                    Commit: () => { settings.Editor.TabSize = size; saveSettings(); },
                    Revert: () => { foreach (var t in tabs) { t.Editor.TabSize = original; t.Editor.InvalidateEditorVisual(); } }
                )).ToList();
            }),

            new("Toggle Block Caret", Action: () =>
            {
                settings.Editor.Caret.BlockCaret = !settings.Editor.Caret.BlockCaret;
                foreach (var t in tabs)
                {
                    t.Editor.BlockCaret = settings.Editor.Caret.BlockCaret;
                    t.Editor.InvalidateEditorVisual();
                }
                saveSettings();
                syncTerm();
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

            new("Workspace: Open Workspace", Action: workspace.Open),

            new("Workspace: Close Workspace", Action: workspace.Close),

            new("Workspace: Add Folder to Workspace", Action: workspace.AddFolder),

            new("Workspace: Save Workspace As...", Action: workspace.SaveAs),

            new("Terminal: Toggle", Action: ctx.Terminal.Toggle),

            new("Terminal: New Session", Action: ctx.Terminal.NewSession),

            new("Editor: Split Group", Action: ctx.EditorSplit.ToggleSplit),

            new("Editor: Join with Sibling", Action: ctx.EditorSplit.JoinWithSibling),

            new("Editor: Join All Groups", Action: ctx.EditorSplit.JoinAll),

            new("Editor: Switch Split Orientation", Action: ctx.EditorSplit.SwitchOrientation),

            new("Editor: Focus Next Group", Action: ctx.EditorSplit.FocusNextLeaf),

            new("Check for Updates", Action: ctx.CheckForUpdates),

            new("Open Recent", Action: ctx.OpenRecent),

            new("Go to Line", Action: () => ctx.CommandPalette.OpenFreeInput("Go to Line: ", text =>
            {
                if (int.TryParse(text.Trim(), out int line) && line >= 1)
                    ctx.ActiveEditor?.GoToLine(line - 1);
            })),

            new("Change Language", CurrentValue: () => activeEditor.LanguageName, GetOptions: () =>
            {
                var activeTab = ctx.Tabs.FirstOrDefault(t => t.Editor == activeEditor);
                var originalOverride = activeTab?.LanguageOverride;
                var options = new List<PaletteOption>
                {
                    new("Plain Text",
                        ApplyPreview: () => ctx.SetLanguage(""),
                        Commit: () => ctx.SetLanguage(""),
                        Revert: () => ctx.SetLanguage(originalOverride))
                };
                options.AddRange(syntaxManager.GetAvailableLanguages().Select(name => new PaletteOption(
                    name,
                    ApplyPreview: () => ctx.SetLanguage(name),
                    Commit: () => ctx.SetLanguage(name),
                    Revert: () => ctx.SetLanguage(originalOverride)
                )));
                return options;
            }),
        ];
    }
}
