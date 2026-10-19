using Declutter.Core.Files;

namespace Declutter.Windows;

/// <summary>
/// Moves files to the Recycle Bin through the Windows shell. If a file can't be recycled (too big, or on
/// a drive without a Recycle Bin), Windows asks the user before deleting it permanently.
/// </summary>
public sealed class ShellRecycler(Func<IntPtr> ownerWindow) : IRecycler
{
    public bool Recycle(string path)
    {
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            hwnd = ownerWindow(),
            wFunc = NativeMethods.FO_DELETE,
            pFrom = path + '\0', // the marshaller adds the second terminating null
            fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT |
                     NativeMethods.FOF_NOERRORUI | NativeMethods.FOF_WANTNUKEWARNING,
        };
        int result = NativeMethods.SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>Recycles a batch on an STA thread, as the shell expects.</summary>
    public Task<RecycleResult> RecycleAsync(UserFileDeleter deleter, IReadOnlyList<FileEntry> files, IProgress<int>? progress, CancellationToken ct) =>
        StaThread.Run(() => deleter.Recycle(files, progress, ct));
}
