using System.Windows;
using Declutter.App.ViewModels;
using Declutter.Core.Explain;

namespace Declutter.App;

public partial class ExplainWindow : Window
{
    private readonly Explainer _explainer;
    private readonly ExplainRequest _request;
    private readonly CancellationTokenSource _cts = new();

    public ExplainWindow(Explainer explainer, ExplainRequest request)
    {
        InitializeComponent();
        _explainer = explainer;
        _request = request;
        Title = "Explain: " + request.Name;
        KindText.Text = request.Kind;
        TitleText.Text = request.Name;
        PreviewText.Text = explainer.Describe(request);
        if (!explainer.IsConfigured)
        {
            ConsentPanel.Visibility = Visibility.Collapsed;
            SetupText.Visibility = Visibility.Visible;
            AskButton.Visibility = Visibility.Collapsed;
            SettingsButton.Visibility = Visibility.Visible;
        }
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        Closed += (_, _) => _cts.Cancel();
    }

    private async void OnAsk(object sender, RoutedEventArgs e)
    {
        ConsentPanel.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        AskButton.IsEnabled = false;
        BusyPanel.Visibility = Visibility.Visible;
        try
        {
            AnswerText.Text = await _explainer.AskAsync(_request, _cts.Token);
            AnswerScroll.Visibility = Visibility.Visible;
            FooterText.Visibility = Visibility.Visible;
            CopyButton.Visibility = Visibility.Visible;
            AskButton.Visibility = Visibility.Collapsed;
        }
        catch (ExplainException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            AskButton.Content = "Try again";
            AskButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // window closed while waiting
        }
        finally
        {
            BusyPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel main) main.CurrentPage = main.Settings;
        Close();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{_request.Name}\n\n{AnswerText.Text}");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // clipboard busy; harmless
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
