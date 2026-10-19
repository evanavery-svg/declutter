using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Declutter.App.ViewModels;

namespace Declutter.App;

/// <summary>
/// "Declutter.exe --smoke-test &lt;dir&gt;" (used by CI): opens every page, runs the read-only scans, runs one
/// real Recycle-Bin delete inside a throwaway test folder, screenshots each page, and exits with code 0
/// only if nothing failed. Data-binding path errors (typos in XAML) count as failures.
/// </summary>
internal sealed class SmokeTest
{
    private readonly List<string> _log = [];
    private readonly BindingErrorListener _bindingErrors = new();
    private bool _failed;

    public SmokeTest(string outputDir)
    {
        OutputDir = outputDir;
        Dialogs = new AutoDialogs(this);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
    }

    public string OutputDir { get; }
    public IDialogs Dialogs { get; }

    public void Log(string line) => _log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");

    public void Fail(string reason)
    {
        _failed = true;
        Log("FAIL: " + reason);
    }

    public async Task RunAsync(MainWindow window, MainViewModel vm)
    {
        string? testFolder = null;
        try
        {
            Directory.CreateDirectory(OutputDir);
            await RenderAsync(window, "1-home");

            vm.CurrentPage = vm.Security;
            await WaitAsync(vm.Security);
            Check(vm.Security.HasRun, "Security check completed");
            Log($"Security: {vm.Security.Summary}");
            foreach (var f in vm.Security.Findings) Log($"  finding [{f.SeverityLabel}] {f.Title}");
            foreach (var p in vm.Security.Passed) Log($"  passed: {p}");
            await RenderAsync(window, "2-security");

            vm.CurrentPage = vm.Junk;
            await WaitAsync(vm.Junk);
            Check(vm.Junk.HasScanned, "Junk scan completed");
            Log($"Junk: {vm.Junk.Summary}");
            await RenderAsync(window, "3-junk");

            vm.CurrentPage = vm.Startup;
            await WaitAsync(vm.Startup);
            Log($"Startup: {vm.Startup.Summary}");
            await RenderAsync(window, "4-startup");

            vm.CurrentPage = vm.Apps;
            await WaitAsync(vm.Apps);
            Check(vm.Apps.Apps.Count > 0, "Apps were listed");
            Log($"Apps: {vm.Apps.Summary}");
            await RenderAsync(window, "5-apps");

            // Files: a throwaway folder in Documents (outside AppData, so the real safety rules apply)
            // with one large file and three identical copies.
            testFolder = Path.Combine(vm.Services.Folders.Documents!, "Declutter smoke test " + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(testFolder, "copies"));
            var copy = new byte[2 * 1024 * 1024];
            new Random(42).NextBytes(copy);
            foreach (var name in new[] { "photo.jpg", "copies\\photo - Copy.jpg", "copies\\photo (2).jpg" })
                await File.WriteAllBytesAsync(Path.Combine(testFolder, name), copy);
            await using (var big = File.Create(Path.Combine(testFolder, "big video.mp4")))
                big.SetLength(60L * 1024 * 1024);

            vm.Files.SetLocations([testFolder]);
            vm.CurrentPage = vm.Files;
            vm.Files.LargeThreshold.Selected = 50L << 20;
            await vm.Files.ScanAsync();
            Check(vm.Files.LargeFiles.Count == 1, "Large file found");
            Check(vm.Files.DuplicateRows.Count == 4, "Duplicate group found (1 header + 3 files)");
            await RenderAsync(window, "6-files-large");
            vm.Files.Tab.Selected = FilesTab.Duplicates;
            await RenderAsync(window, "7-files-duplicates");
            vm.Files.Tab.Selected = FilesTab.Folders;
            await RenderAsync(window, "8-files-folders");

            vm.Files.SelectExtraCopiesCommand.Execute(null);
            Check(vm.Files.SelectedCount == 2, "Extra copies selected (keeper left unselected)");
            await vm.Files.RecycleCommand.ExecuteAsync();
            Check(File.Exists(Path.Combine(testFolder, "photo.jpg")), "The suggested keeper still exists");
            Check(!File.Exists(Path.Combine(testFolder, "copies", "photo - Copy.jpg")), "Extra copy 1 was recycled");
            Check(!File.Exists(Path.Combine(testFolder, "copies", "photo (2).jpg")), "Extra copy 2 was recycled");

            var dialog = new DialogWindow("Clean 1.2 GB?", "This is what a confirmation looks like.\n\n•  Temporary files: 1.0 GB\n•  Microsoft Edge cache: 200 MB", "Clean now", "Cancel", false)
            {
                Owner = window,
            };
            dialog.Show();
            await RenderAsync(dialog, "9-dialog");
            dialog.Close();

            vm.CurrentPage = vm.Home;
            await RenderAsync(window, "10-home-after");
        }
        catch (Exception ex)
        {
            Fail(ex.ToString());
        }
        finally
        {
            if (testFolder != null)
            {
                try { Directory.Delete(testFolder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        foreach (var error in _bindingErrors.Errors)
        {
            if (error.Contains("BindingExpression path error", StringComparison.Ordinal)) Fail("Binding: " + error);
            else Log("Binding warning: " + error);
        }

        Log(_failed ? "SMOKE TEST FAILED" : "SMOKE TEST PASSED");
        await File.WriteAllLinesAsync(Path.Combine(OutputDir, "smoke-test.log"), _log);
        Application.Current.Shutdown(_failed ? 1 : 0);
    }

    private void Check(bool condition, string what)
    {
        if (condition) Log("ok: " + what);
        else Fail(what);
    }

    private async Task WaitAsync(PageViewModel page)
    {
        var timeout = Stopwatch.StartNew();
        await Task.Delay(200);
        while (page.IsBusy)
        {
            if (timeout.Elapsed > TimeSpan.FromMinutes(3))
            {
                Fail($"{page.Title} page didn't finish within 3 minutes");
                return;
            }
            await Task.Delay(100);
        }
    }

    private async Task RenderAsync(Window window, string name)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        if (window.Content is not FrameworkElement root || root.ActualWidth < 1) return;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Background first, then the window content itself (a VisualBrush would rescale to the content's bounds).
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var file = File.Create(Path.Combine(OutputDir, name + ".png"));
        encoder.Save(file);
        Log("screenshot: " + name);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly System.Text.StringBuilder _current = new();
        public List<string> Errors { get; } = [];
        public override void Write(string? message) => _current.Append(message);

        public override void WriteLine(string? message)
        {
            _current.Append(message);
            Errors.Add(_current.ToString());
            _current.Clear();
        }
    }

    /// <summary>Answers dialogs automatically. Only the Recycle Bin step of the test is ever confirmed.</summary>
    private sealed class AutoDialogs(SmokeTest test) : IDialogs
    {
        public bool Confirm(string title, string message, string confirmText, bool danger = false)
        {
            bool yes = title == "Move to Recycle Bin?";
            test.Log($"dialog (confirm → {(yes ? "yes" : "no")}): {title}");
            return yes;
        }

        public void Show(string title, string message) => test.Log($"dialog: {title}: {message}");
    }
}
