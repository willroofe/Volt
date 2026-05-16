using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public sealed record IssueNavigationRequest(int Line, int Column);

public sealed class IssueRow
{
    public IssueRow(TabInfo tab, ParseDiagnostic diagnostic)
    {
        Diagnostic = diagnostic;
        SeverityKind = diagnostic.Severity;
        FileName = tab.DisplayName;
        Line = diagnostic.Range.Start.Line;
        Column = diagnostic.Range.Start.Column;
        Location = $"Ln {Line + 1}, Col {Column + 1}";
        Message = diagnostic.Message;
        Severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => "Error",
            DiagnosticSeverity.Warning => "Warning",
            DiagnosticSeverity.Information => "Info",
            _ => diagnostic.Severity.ToString()
        };
        SeverityGlyph = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => Codicons.ChromeClose,
            DiagnosticSeverity.Warning => Codicons.Info,
            DiagnosticSeverity.Information => Codicons.Info,
            _ => Codicons.Info
        };
        SeverityBrush = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => ErrorBrush,
            DiagnosticSeverity.Warning => WarningBrush,
            DiagnosticSeverity.Information => InfoBrush,
            _ => InfoBrush
        };
    }

    private static readonly Brush ErrorBrush = CreateBrush(Color.FromRgb(224, 82, 82));
    private static readonly Brush WarningBrush = CreateBrush(Color.FromRgb(209, 154, 102));
    private static readonly Brush InfoBrush = CreateBrush(Color.FromRgb(86, 182, 194));

    public ParseDiagnostic Diagnostic { get; }
    public DiagnosticSeverity SeverityKind { get; }
    public string Severity { get; }
    public string SeverityGlyph { get; }
    public Brush SeverityBrush { get; }
    public string FileName { get; }
    public string Location { get; }
    public string Message { get; }
    public int Line { get; }
    public int Column { get; }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public partial class IssuesPanel : UserControl, IPanel
{
    public static readonly DependencyProperty IssueRowsProperty =
        DependencyProperty.Register(
            nameof(IssueRows),
            typeof(ObservableCollection<IssueRow>),
            typeof(IssuesPanel),
            new PropertyMetadata(null));

    private TabInfo? _activeTab;
    private bool _suppressMouseBringIntoView;

    public string PanelId => "issues";
    public string Title => "Issues";
    public string? IconGlyph => Codicons.ListSelection;
    public new UIElement Content => this;
#pragma warning disable CS0067 // Title is fixed; event required by IPanel for other panels
    public event Action? TitleChanged;
#pragma warning restore CS0067

    public event Action<IssueNavigationRequest>? IssueNavigationRequested;

    public ObservableCollection<IssueRow> IssueRows
    {
        get => (ObservableCollection<IssueRow>)GetValue(IssueRowsProperty);
        private set => SetValue(IssueRowsProperty, value);
    }

    internal IReadOnlyList<IssueRow> Rows => IssueRows;
    internal string Summary => SummaryText.Text;
    internal string Status => StatusText.Text;

    public IssuesPanel()
    {
        IssueRows = [];
        InitializeComponent();
        Refresh();
    }

    public void SetActiveTab(TabInfo? tab)
    {
        if (ReferenceEquals(_activeTab, tab))
        {
            Refresh();
            return;
        }

        if (_activeTab != null)
            _activeTab.Editor.DiagnosticsChanged -= OnDiagnosticsChanged;

        _activeTab = tab;

        if (_activeTab != null)
            _activeTab.Editor.DiagnosticsChanged += OnDiagnosticsChanged;

        Refresh();
    }

    internal void Refresh()
    {
        IssueRows.Clear();

        if (_activeTab == null)
        {
            SummaryText.Text = "No issues";
            StatusText.Text = "No active editor";
            return;
        }

        if (_activeTab.IsLoading)
        {
            SummaryText.Text = "No issues";
            StatusText.Text = "Loading...";
            return;
        }

        if (_activeTab.IsSaving)
        {
            SummaryText.Text = "No issues";
            StatusText.Text = "Saving...";
            return;
        }

        EditorControl editor = _activeTab.Editor;
        DiagnosticsStatusInfo status = editor.DiagnosticsStatus;
        if (status.Kind is DiagnosticsStatusKind.Checking or DiagnosticsStatusKind.Disabled)
        {
            SummaryText.Text = "No issues";
            StatusText.Text = status.Text;
            return;
        }

        foreach (IssueRow row in editor.Diagnostics
                     .OrderBy(diagnostic => GetSeveritySortOrder(diagnostic.Severity))
                     .ThenBy(diagnostic => diagnostic.Range.Start.Line)
                     .ThenBy(diagnostic => diagnostic.Range.Start.Column)
                     .Select(diagnostic => new IssueRow(_activeTab, diagnostic)))
        {
            IssueRows.Add(row);
        }

        SummaryText.Text = CreateSummaryText(IssueRows);
        StatusText.Text = editor.HasMoreDiagnostics ? "Showing first 1000 issues" : "";
    }

    internal void NavigateIssue(IssueRow row)
        => IssueNavigationRequested?.Invoke(new IssueNavigationRequest(row.Line, row.Column));

    private void OnDiagnosticsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Background);

    private void OnIssuesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IssuesList.SelectedItem is not IssueRow row)
            return;

        NavigateIssue(row);
        e.Handled = true;
    }

    private void OnIssueItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _suppressMouseBringIntoView = true;
        Dispatcher.BeginInvoke(
            new Action(() => _suppressMouseBringIntoView = false),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnIssueItemRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (!ShouldHandleIssueItemRequestBringIntoView())
            return;

        e.Handled = true;
    }

    private bool ShouldHandleIssueItemRequestBringIntoView()
        => _suppressMouseBringIntoView;

    private static int GetSeveritySortOrder(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => 0,
        DiagnosticSeverity.Warning => 1,
        DiagnosticSeverity.Information => 2,
        _ => 3
    };

    private static string CreateSummaryText(IEnumerable<IssueRow> rows)
    {
        int errors = 0;
        int warnings = 0;
        int infos = 0;

        foreach (IssueRow row in rows)
        {
            switch (row.SeverityKind)
            {
                case DiagnosticSeverity.Error:
                    errors++;
                    break;
                case DiagnosticSeverity.Warning:
                    warnings++;
                    break;
                case DiagnosticSeverity.Information:
                    infos++;
                    break;
            }
        }

        if (errors == 0 && warnings == 0 && infos == 0)
            return "No issues";

        var parts = new List<string>(3);
        if (errors > 0)
            parts.Add(FormatCount(errors, "error"));
        if (warnings > 0)
            parts.Add(FormatCount(warnings, "warning"));
        if (infos > 0)
            parts.Add(FormatCount(infos, "info"));

        return string.Join(", ", parts);
    }

    private static string FormatCount(int count, string singular)
        => count == 1 ? $"1 {singular}" : $"{count} {singular}s";
}
