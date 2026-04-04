using System.Windows;
using System.Windows.Controls;

namespace Volt;

public partial class ThemedMessageBox : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    private ThemedMessageBox()
    {
        InitializeComponent();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.Cancel;
        DialogResult = false;
    }

    private void AddButton(string text, MessageBoxResult result, bool isDefault = false, bool isCancel = false)
    {
        var btn = new Button
        {
            Content = text,
            Style = (Style)FindResource("DialogButton"),
            IsDefault = isDefault,
            IsCancel = isCancel
        };
        btn.Click += (_, _) =>
        {
            Result = result;
            DialogResult = result != MessageBoxResult.Cancel;
        };
        ButtonPanel.Children.Add(btn);
    }

    public static MessageBoxResult Show(Window owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK)
    {
        var dlg = new ThemedMessageBox { Owner = owner };
        if (!string.IsNullOrEmpty(title)) dlg.Title = title;
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;

        switch (buttons)
        {
            case MessageBoxButton.OK:
                dlg.AddButton("OK", MessageBoxResult.OK, isDefault: true, isCancel: true);
                break;
            case MessageBoxButton.OKCancel:
                dlg.AddButton("OK", MessageBoxResult.OK, isDefault: true);
                dlg.AddButton("Cancel", MessageBoxResult.Cancel, isCancel: true);
                break;
            case MessageBoxButton.YesNo:
                dlg.AddButton("Yes", MessageBoxResult.Yes, isDefault: true);
                dlg.AddButton("No", MessageBoxResult.No, isCancel: true);
                break;
            case MessageBoxButton.YesNoCancel:
                dlg.AddButton("Yes", MessageBoxResult.Yes, isDefault: true);
                dlg.AddButton("No", MessageBoxResult.No);
                dlg.AddButton("Cancel", MessageBoxResult.Cancel, isCancel: true);
                break;
        }

        dlg.ShowDialog();
        return dlg.Result;
    }
}
