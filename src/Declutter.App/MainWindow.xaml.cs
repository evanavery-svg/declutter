using System.Windows;
using System.Windows.Interop;

namespace Declutter.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Fit smaller laptop screens (e.g. 1366×768) instead of opening partly off-screen.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width * 0.94);
        Height = Math.Min(Height, work.Height * 0.94);
        MinWidth = Math.Min(MinWidth, work.Width);
        MinHeight = Math.Min(MinHeight, work.Height);
        // Badge colours are picked in code; rebuild the visible page so they follow a theme change.
        WindowTheme.Changed += (_, _) =>
        {
            // SetCurrentValue (not assignment) keeps the {Binding CurrentPage} intact.
            object page = PageHost.Content;
            PageHost.SetCurrentValue(System.Windows.Controls.ContentControl.ContentProperty, null);
            PageHost.SetCurrentValue(System.Windows.Controls.ContentControl.ContentProperty, page);
        };
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            WindowTheme.Apply(this);
        };
    }

    /// <summary>
    /// The window handle, captured once on the UI thread so background shell operations (Recycle Bin)
    /// can use it as the owner of any prompt Windows shows.
    /// </summary>
    public IntPtr Handle { get; private set; }
}
