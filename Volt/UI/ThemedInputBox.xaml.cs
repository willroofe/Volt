using System.Windows;

namespace Volt;

public partial class ThemedInputBox : Window
{
    private ThemedInputBox(string title, string prompt, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        InputTextBox.Text = defaultValue;
        InputTextBox.SelectAll();
        InputTextBox.Focus();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    public static string? Show(Window owner, string title, string prompt, string defaultValue = "")
    {
        var dlg = new ThemedInputBox(title, prompt, defaultValue) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.InputTextBox.Text : null;
    }
}
