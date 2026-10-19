using System.Windows;

namespace Declutter.App;

public partial class DialogWindow : Window
{
    public DialogWindow(string title, string message, string primary, string? secondary, bool danger)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        if (danger) PrimaryButton.Style = (Style)FindResource("DangerButton");
        if (secondary == null)
            SecondaryButton.Visibility = Visibility.Collapsed;
        else
            SecondaryButton.Content = secondary;
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnSecondary(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class WindowDialogs : IDialogs
{
    public bool Confirm(string title, string message, string confirmText, bool danger = false) =>
        ShowDialog(new DialogWindow(title, message, confirmText, "Cancel", danger));

    public void Show(string title, string message) => ShowDialog(new DialogWindow(title, message, "OK", null, false));

    private static bool ShowDialog(DialogWindow dialog)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }
}
