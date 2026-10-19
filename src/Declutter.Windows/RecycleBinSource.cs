using Declutter.Core.Junk;
using Declutter.Core.Util;

namespace Declutter.Windows;

/// <summary>The Recycle Bin, measured and emptied through the shell (never by deleting $Recycle.Bin).</summary>
public sealed class RecycleBinSource(Func<IntPtr> ownerWindow) : IJunkSource
{
    public string Id => "recycle-bin";
    public string Name => "Recycle Bin";
    public string Description => "Files you've already deleted. Emptying it removes them for good.";
    public string? Warning => "This can't be undone. Check the Recycle Bin first if you're unsure.";
    public bool SelectedByDefault => false;
    public bool RequiresAdmin => false;
    public IReadOnlyList<BlockingProcess> BlockingProcesses => [];

    public Task<JunkScanResult> ScanAsync(JunkContext context, CancellationToken ct) => Task.Run(() =>
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        int hr = NativeMethods.SHQueryRecycleBin(null, ref info);
        return hr == 0
            ? new JunkScanResult([], info.i64Size, (int)Math.Min(info.i64NumItems, int.MaxValue), null)
            : new JunkScanResult([], 0, 0, "Couldn't read the Recycle Bin.");
    }, ct);

    public async Task<JunkCleanResult> CleanAsync(JunkScanResult scan, JunkContext context, IProgress<long>? bytesFreed, CancellationToken ct)
    {
        IntPtr owner = ownerWindow();
        int hr = await StaThread.Run(() => NativeMethods.SHEmptyRecycleBin(owner, null,
            NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND));

        // E_UNEXPECTED means it was already empty.
        bool ok = hr == 0 || hr == unchecked((int)0x8000FFFF);
        var result = ok
            ? new JunkCleanResult { BytesFreed = scan.Bytes, Deleted = scan.Count }
            : new JunkCleanResult { Skipped = scan.Count };
        bytesFreed?.Report(result.BytesFreed);
        context.Log.Write("Junk", ok
            ? $"Emptied the Recycle Bin ({scan.Count} items, {ByteSize.Format(scan.Bytes)})"
            : $"Emptying the Recycle Bin failed (0x{hr:X8})");
        return result;
    }
}
