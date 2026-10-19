using System.Windows;
using System.Windows.Interop;

namespace Declutter.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;
}
