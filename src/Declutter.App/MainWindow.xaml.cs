using System.Windows;
using System.Windows.Interop;

namespace Declutter.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
