using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace Volt.Tests;

[Collection("WpfApplication")]
public class IssuesPanelTests
{
    private static readonly object AppGate = new();

    [StaFact]
    public void Diagnostics_ExposesOnlyCompletedDiagnostics()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetLanguage(new JsonLanguageService());
        var diagnostic = new ParseDiagnostic(
            TextRange.FromBounds(0, 1, 0, 2),
            DiagnosticSeverity.Error,
            "Expected value.");

        SetDiagnostics(editor, [diagnostic], isComplete: false, hasMoreDiagnostics: true);

        Assert.Empty(editor.Diagnostics);
        Assert.Equal(0, editor.DiagnosticCount);
        Assert.False(editor.HasMoreDiagnostics);

        SetDiagnostics(editor, [diagnostic], isComplete: true, hasMoreDiagnostics: true);

        Assert.Equal([diagnostic], editor.Diagnostics);
        Assert.Equal(1, editor.DiagnosticCount);
        Assert.True(editor.HasMoreDiagnostics);
    }

    [StaFact]
    public void GoToPosition_ClampsCaretAndCentersViewport()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetContent(string.Join('\n', Enumerable.Range(0, 40).Select(i => new string('x', 120))));
        editor.Measure(new Size(320, 120));
        editor.Arrange(new Rect(0, 0, 320, 120));
        editor.UpdateLayout();

        editor.GoToPosition(20, 80);

        Assert.Equal(20, editor.CaretLine);
        Assert.Equal(80, editor.CaretCol);
        Assert.True(editor.VerticalOffset > 0);
        Assert.True(editor.HorizontalOffset > 0);

        editor.GoToPosition(999, 999);

        Assert.Equal(39, editor.CaretLine);
        Assert.Equal(120, editor.CaretCol);
    }

    [StaFact]
    public void SetActiveTab_FormatsRowsAndSummary()
    {
        EnsureWpfResources();
        var panel = new IssuesPanel();
        var tab = CreateTab(@"C:\project\settings.json");
        var error = new ParseDiagnostic(
            TextRange.FromBounds(2, 4, 2, 5),
            DiagnosticSeverity.Error,
            "Expected ':'.");
        var warning = new ParseDiagnostic(
            TextRange.FromBounds(0, 1, 0, 2),
            DiagnosticSeverity.Warning,
            "Deprecated setting.");
        var info = new ParseDiagnostic(
            TextRange.FromBounds(1, 0, 1, 1),
            DiagnosticSeverity.Information,
            "Schema hint.");
        SetDiagnostics(tab.Editor, [info, warning, error], isComplete: true, hasMoreDiagnostics: false);

        panel.SetActiveTab(tab);

        Assert.Equal("1 error, 1 warning, 1 info", panel.Summary);
        Assert.Equal("", panel.Status);
        Assert.Equal([DiagnosticSeverity.Error, DiagnosticSeverity.Warning, DiagnosticSeverity.Information],
            panel.Rows.Select(row => row.SeverityKind));
        Assert.Equal("settings.json", panel.Rows[0].FileName);
        Assert.Equal("Ln 3, Col 5", panel.Rows[0].Location);
        Assert.Equal("Expected ':'.", panel.Rows[0].Message);
    }

    [StaFact]
    public void Refresh_ShowsCheckingDisabledAndTruncatedStates()
    {
        EnsureWpfResources();
        var panel = new IssuesPanel();
        var tab = CreateTab();
        SetDiagnostics(
            tab.Editor,
            [],
            isComplete: false,
            hasMoreDiagnostics: false,
            progress: new LanguageDiagnosticsProgress(1, 8));

        panel.SetActiveTab(tab);

        Assert.Equal("No issues", panel.Summary);
        Assert.Equal("Checking JSON 12.5%", panel.Status);

        SetDiagnostics(
            tab.Editor,
            [new ParseDiagnostic(TextRange.FromBounds(0, 0, 0, 1), DiagnosticSeverity.Error, "Too much.")],
            isComplete: true,
            hasMoreDiagnostics: true);
        panel.Refresh();

        Assert.Equal("1 error", panel.Summary);
        Assert.Equal("Showing first 1000 issues", panel.Status);

        SetPrivateField(tab.Editor, "_diagnosticsDisabledMessage", "JSON checking disabled for files over 50 MiB");
        panel.Refresh();

        Assert.Equal("No issues", panel.Summary);
        Assert.Equal("JSON checking disabled for files over 50 MiB", panel.Status);
        Assert.Empty(panel.Rows);
    }

    [StaFact]
    public void SetActiveTab_UnsubscribesPreviousEditor()
    {
        EnsureWpfResources();
        var panel = new IssuesPanel();
        var first = CreateTab(@"C:\project\first.json");
        var second = CreateTab(@"C:\project\second.json");

        panel.SetActiveTab(first);

        Assert.Single(GetDiagnosticsHandlers(first.Editor));

        panel.SetActiveTab(second);

        Assert.Empty(GetDiagnosticsHandlers(first.Editor));
        Assert.Single(GetDiagnosticsHandlers(second.Editor));
    }

    [StaFact]
    public void NavigateIssue_RaisesIssueNavigationRequest()
    {
        EnsureWpfResources();
        var panel = new IssuesPanel();
        var tab = CreateTab();
        SetDiagnostics(
            tab.Editor,
            [new ParseDiagnostic(TextRange.FromBounds(4, 7, 4, 8), DiagnosticSeverity.Error, "Expected value.")],
            isComplete: true,
            hasMoreDiagnostics: false);
        panel.SetActiveTab(tab);

        IssueNavigationRequest? request = null;
        panel.IssueNavigationRequested += value => request = value;

        panel.NavigateIssue(panel.Rows[0]);

        Assert.Equal(new IssueNavigationRequest(4, 7), request);
    }

    [StaFact]
    public void RefreshActiveTab_UpdatesRowFileNameAfterPathChanges()
    {
        EnsureWpfResources();
        var panel = new IssuesPanel();
        var tab = CreateTab(@"C:\project\old.json");
        SetDiagnostics(
            tab.Editor,
            [new ParseDiagnostic(TextRange.FromBounds(0, 0, 0, 1), DiagnosticSeverity.Error, "Expected value.")],
            isComplete: true,
            hasMoreDiagnostics: false);
        panel.SetActiveTab(tab);

        Assert.Equal("old.json", panel.Rows[0].FileName);

        tab.FilePath = @"C:\project\new.json";
        panel.RefreshActiveTab();

        Assert.Equal("new.json", panel.Rows[0].FileName);
    }

    [StaFact]
    public void MouseSelection_SuppressesRowBringIntoViewUntilInputCompletes()
    {
        EnsureWpfResources();
        var panel = new IssuesPanel();

        InvokePrivate(panel, "OnIssueItemPreviewMouseLeftButtonDown", panel, null!);

        Assert.True((bool)InvokePrivate(panel, "ShouldHandleIssueItemRequestBringIntoView")!);

        DrainDispatcher();

        Assert.False((bool)InvokePrivate(panel, "ShouldHandleIssueItemRequestBringIntoView")!);
    }

    private static TabInfo CreateTab(string? filePath = null)
    {
        var tab = new TabInfo(new ThemeManager(), new LanguageManager())
        {
            FilePath = filePath
        };
        tab.Editor.SetLanguage(new JsonLanguageService());
        return tab;
    }

    private static void SetDiagnostics(
        EditorControl editor,
        IReadOnlyList<ParseDiagnostic> diagnostics,
        bool isComplete,
        bool hasMoreDiagnostics,
        LanguageDiagnosticsProgress? progress = null)
    {
        SetPrivateField(editor, "_diagnosticsDisabledMessage", "");
        SetPrivateField(editor, "_diagnosticsProgress", progress);
        SetPrivateField(editor, "_diagnosticsSnapshot", new LanguageDiagnosticsSnapshot(
            "JSON",
            SourceVersion: 1,
            diagnostics,
            isComplete,
            progress,
            hasMoreDiagnostics));
    }

    private static IReadOnlyList<Delegate> GetDiagnosticsHandlers(EditorControl editor)
    {
        var field = typeof(EditorControl).GetField(
            "DiagnosticsChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return ((MulticastDelegate?)field.GetValue(editor))?.GetInvocationList() ?? [];
    }

    private static void SetPrivateField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(instance, value);
    }

    private static object? InvokePrivate(object instance, string name, params object?[] args)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(instance, args);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void EnsureWpfResources()
    {
        lock (AppGate)
        {
            if (Application.Current == null)
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            var resources = Application.Current!.Resources;
            resources["ThemedScrollViewer"] = CreateScrollViewerTemplate();
            resources["ThemeContentBg"] = Brushes.White;
            resources["ThemeExplorerHeaderBg"] = Brushes.WhiteSmoke;
            resources["ThemeTabBorder"] = Brushes.LightGray;
            resources["ThemeTextFg"] = Brushes.Black;
            resources["ThemeTextFgMuted"] = Brushes.Gray;
            resources["ThemeExplorerItemHover"] = Brushes.Gainsboro;
            resources["ThemeExplorerItemSelected"] = Brushes.LightGray;
        }
    }

    private static ControlTemplate CreateScrollViewerTemplate()
    {
        const string xaml =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "TargetType='ScrollViewer'><Border><ScrollContentPresenter/></Border></ControlTemplate>";
        return (ControlTemplate)XamlReader.Parse(xaml);
    }
}
