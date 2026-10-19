using Declutter.Core.Safety;
using Declutter.Core.Util;

namespace Declutter.Core.Files;

/// <summary>Sends a file to the Recycle Bin. Implemented with the Windows shell on Windows.</summary>
public interface IRecycler
{
    /// <summary>Returns false if the file could not be recycled (in use, cancelled, …).</summary>
    bool Recycle(string path);
}

public sealed class RecycleResult
{
    public int Recycled { get; set; }
    public long BytesRecycled { get; set; }
    public List<(string Path, string Reason)> Failures { get; } = [];
}

/// <summary>Deletes personal files — only ever to the Recycle Bin, and only after the safety check.</summary>
public sealed class UserFileDeleter
{
    private readonly SafetyGuard _guard;
    private readonly IRecycler _recycler;
    private readonly IActivityLog _log;

    public UserFileDeleter(SafetyGuard guard, IRecycler recycler, IActivityLog log)
    {
        _guard = guard;
        _recycler = recycler;
        _log = log;
    }

    public RecycleResult Recycle(IEnumerable<FileEntry> files, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var result = new RecycleResult();
        int done = 0;
        foreach (var file in files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(++done);

            var verdict = _guard.Check(file.Path, DeletionKind.UserFile);
            if (!verdict.Allowed)
            {
                result.Failures.Add((file.Path, verdict.Reason ?? "Blocked by safety check."));
                _log.Write("Files", $"REFUSED {file.Path}: {verdict.Reason}");
                continue;
            }

            var info = new FileInfo(file.Path);
            if (!info.Exists)
            {
                result.Failures.Add((file.Path, "The file no longer exists."));
                continue;
            }
            if (FileSystemSafety.IsLink(info))
            {
                result.Failures.Add((file.Path, "Links are never deleted."));
                continue;
            }

            if (_recycler.Recycle(file.Path) && !File.Exists(file.Path))
            {
                result.Recycled++;
                result.BytesRecycled += file.Size;
                _log.Write("Files", $"Recycled {file.Path} ({ByteSize.Format(file.Size)})");
            }
            else
            {
                result.Failures.Add((file.Path, "Windows couldn't move it to the Recycle Bin (it may be open in another app)."));
                _log.Write("Files", $"FAILED to recycle {file.Path}");
            }
        }
        return result;
    }

    /// <summary>Refuses a selection that would delete every copy of some duplicate group.</summary>
    public static string? ValidateDuplicateSelection(IEnumerable<DuplicateGroup> groups, IReadOnlySet<string> selectedPaths)
    {
        foreach (var g in groups)
        {
            if (g.Files.All(f => selectedPaths.Contains(f.Path)))
                return $"Every copy of \"{g.Files[0].Name}\" is selected. Keep at least one copy.";
        }
        return null;
    }
}
