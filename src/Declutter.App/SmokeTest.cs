using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

            vm.CurrentPage = vm.Health;
            await WaitAsync(vm.Health);
            Check(vm.Health.HasRun, "Health check completed");
            foreach (var card in vm.Health.Disks.Concat(vm.Health.Batteries)) Log($"  health: {card.Name}: {card.GradeLabel} ({card.Summary})");
            Log($"  battery: {vm.Health.BatteryMessage}");
            await RenderAsync(window, "2b-health");

            await vm.Updates.CheckAsync(userAsked: true);
            Log($"Update check: {(vm.Updates.IsAvailable ? vm.Updates.AvailableText : vm.Updates.Status)}");

            // Self-update renames the running exe out of the way. Prove Windows allows that for this very build.
            if (Environment.ProcessPath is { } exe && exe.EndsWith("Declutter.exe", StringComparison.OrdinalIgnoreCase))
            {
                string probe = exe + ".renametest";
                File.Move(exe, probe);
                File.Move(probe, exe);
                Check(File.Exists(exe), "The running Declutter.exe can be renamed (needed for self-update)");
            }

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

            vm.CurrentPage = vm.Speed;
            await WaitAsync(vm.Speed);
            Check(vm.Speed.HasRun, "Speed check completed");
            Log($"Speed: {vm.Speed.BootHeadline} ({vm.Speed.BootVerdict}); {vm.Speed.ServiceRows.Count} app services, {vm.Speed.SuggestedCount} suggested");
            foreach (var c in vm.Speed.Culprits) Log($"  slowed by {c.Name} ({c.Kind}) +{c.AverageSeconds:0.0}s x{c.Times}");
            foreach (var n in vm.Speed.Hardware) Log($"  hardware: {n.Text}");
            await RenderAsync(window, "10b-speed");

            vm.CurrentPage = vm.ThisPc;
            await WaitAsync(vm.ThisPc);
            Check(vm.ThisPc.Rows.Count >= 3, "This PC details were read");
            Log($"This PC: {vm.ThisPc.Heading}; " + string.Join("; ", vm.ThisPc.Rows.Select(r => $"{r.Label}: {r.Value}")));
            await RenderAsync(window, "11-this-pc");

            string report = await vm.Services.Reports.CreateAsync(new Progress<string>(_ => { }), CancellationToken.None);
            string html = await File.ReadAllTextAsync(report);
            Check(html.Contains("<h1>") && html.Contains("Security"), "Shareable report was created");
            Check(!html.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "Report doesn't contain the Windows user name");
            File.Delete(report);

            vm.CurrentPage = vm.Settings;
            await RenderAsync(window, "12-settings");

            // Live theme switch: everything must stay readable without reopening the page.
            var before = WindowTheme.Mode;
            WindowTheme.SetMode(WindowTheme.IsDark ? ThemeMode.Light : ThemeMode.Dark);
            await RenderAsync(window, "13-settings-switched-theme");
            WindowTheme.SetMode(before);
            vm.CurrentPage = vm.Junk;
            await RenderAsync(window, "14-after-theme-switch");
            Check(window.PageHost.Content == vm.Junk, "Navigation still works after a theme switch");

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
        CheckContrast(window, name);
    }

    /// <summary>
    /// Fails the test for any visible text that's hard to read against what's behind it (WCAG contrast
    /// below 3:1), e.g. black text left on a dark background. Runs on every screenshot, in both themes.
    /// </summary>
    private void CheckContrast(Window window, string page)
    {
        if (window.Content is not DependencyObject root) return;
        var reported = new HashSet<string>();
        foreach (var tb in Descendants(root).OfType<TextBlock>())
        {
            if (!tb.IsVisible || !tb.IsEnabled || tb.ActualWidth < 1 || string.IsNullOrWhiteSpace(tb.Text)) continue;
            if (tb.Foreground is not SolidColorBrush fg || EffectiveOpacity(tb) < 0.9) continue;
            var bg = BackgroundBehind(tb, window);
            if (bg == null) continue;

            double ratio = ContrastRatio(fg.Color, bg.Value);
            string text = tb.Text.Length > 40 ? tb.Text[..40] + "…" : tb.Text;
            if (ratio < 3.0 && reported.Add(text))
                Fail($"{page}: hard to read \"{text}\": {fg.Color} on {bg.Value}, contrast {ratio:0.0}:1");
        }
        Log($"contrast checked: {page}");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static double EffectiveOpacity(DependencyObject element)
    {
        double opacity = 1;
        for (var e = element; e != null; e = VisualTreeHelper.GetParent(e))
            if (e is UIElement u) opacity *= u.Opacity;
        return opacity;
    }

    /// <summary>The first solid, mostly opaque background found walking up from an element.</summary>
    private static Color? BackgroundBehind(DependencyObject element, Window window)
    {
        for (var e = VisualTreeHelper.GetParent(element); e != null; e = VisualTreeHelper.GetParent(e))
        {
            Brush? brush = e switch
            {
                Border b => b.Background,
                Panel p => p.Background,
                Control c => c.Background,
                _ => null,
            };
            if (brush is SolidColorBrush { Color.A: > 200 } solid) return solid.Color;
        }
        return (window.Background as SolidColorBrush)?.Color;
    }

    internal static double ContrastRatio(Color a, Color b)
    {
        static double Channel(byte c)
        {
            double v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
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
