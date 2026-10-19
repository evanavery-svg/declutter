using System.Windows;
using System.Windows.Threading;
using Declutter.App.ViewModels;

namespace Declutter.App;

public partial class App : Application
{
    private static AppServices? _services;

    /// <summary>Set when running with --smoke-test (CI): errors are recorded instead of shown.</summary>
    internal static SmokeTest? Smoke { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WindowTheme.Load(this);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _services?.Log.Write("Error", "Background task failed: " + args.Exception);
            args.SetObserved();
        };

        int smokeIndex = Array.IndexOf(e.Args, "--smoke-test");
        if (smokeIndex >= 0)
            Smoke = new SmokeTest(smokeIndex + 1 < e.Args.Length ? e.Args[smokeIndex + 1] : Path.Combine(Path.GetTempPath(), "declutter-smoke"));

        var window = new MainWindow();
        MainWindow = window;
        try
        {
            _services = new AppServices(Smoke?.Dialogs ?? new WindowDialogs(), () => window.Handle);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Declutter couldn't start:\n\n" + ex.Message, "Declutter", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var main = new MainViewModel(_services);
        window.DataContext = main;
        _services.Log.Write("App", $"Started {main.Version} ({(_services.IsElevated ? "administrator" : "standard user")})");
        window.Show();

        if (Smoke != null) _ = Smoke.RunAsync(window, main);
    }

    /// <summary>Shows an unexpected error without crashing, and logs it.</summary>
    public static void ReportError(Exception ex)
    {
        _services?.Log.Write("Error", ex.ToString());
        if (Smoke != null)
        {
            Smoke.Fail("Unhandled error: " + ex);
            return;
        }
        _services?.Dialogs.Show("Something went wrong", ex.Message + "\n\nNothing was changed by this error. Details are in the activity log.");
    }

    public static void RunOnUi(Action action) => Current?.Dispatcher.BeginInvoke(action);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportError(e.Exception);
        e.Handled = true;
    }
}
