using Declutter.Core.Safety;
using Declutter.Core.Util;

namespace Declutter.Core.Junk;

/// <summary>One folder (or folder pattern) of junk, e.g. "%TEMP%, files older than 2 days".</summary>
public sealed record JunkRule(string Root)
{
    /// <summary>Folder path; a segment of "*" matches any subfolder (browser profiles etc.).</summary>
    public string Root { get; init; } = Root;
    public string SearchPattern { get; init; } = "*";
    public bool Recursive { get; init; } = true;

    /// <summary>Only files untouched for at least this long are removed (protects in-use temp files).</summary>
    public TimeSpan? MinAge { get; init; }

    public bool RemoveEmptyFolders { get; init; } = true;
    public bool RequiresAdmin { get; init; }
}

/// <summary>A running program that keeps a category's files locked.</summary>
public sealed record BlockingProcess(string ProcessName, string DisplayName);

public sealed record JunkItem(string Path, long Size, string Root, TimeSpan? MinAge);

public sealed class JunkScanResult
{
    public static readonly JunkScanResult Empty = new([], 0, 0, null);

    public JunkScanResult(IReadOnlyList<JunkItem> items, long bytes, int count, string? note)
    {
        Items = items;
        Bytes = bytes;
        Count = count;
        Note = note;
    }

    public IReadOnlyList<JunkItem> Items { get; }
    public long Bytes { get; }
    public int Count { get; }

    /// <summary>Extra info shown under the category, e.g. "Some folders need administrator rights".</summary>
    public string? Note { get; }
}

public sealed class JunkCleanResult
{
    public long BytesFreed { get; set; }
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public int Refused { get; set; }
}

public sealed record JunkContext(SafetyGuard Guard, bool IsElevated, IClock Clock, IActivityLog Log);

/// <summary>Anything the Junk Cleaner can measure and clean: a set of folders, or the Recycle Bin.</summary>
public interface IJunkSource
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string? Warning { get; }
    bool SelectedByDefault { get; }

    /// <summary>True if nothing at all can be cleaned without administrator rights.</summary>
    bool RequiresAdmin { get; }

    IReadOnlyList<BlockingProcess> BlockingProcesses { get; }

    Task<JunkScanResult> ScanAsync(JunkContext context, CancellationToken ct);
    Task<JunkCleanResult> CleanAsync(JunkScanResult scan, JunkContext context, IProgress<long>? bytesFreed, CancellationToken ct);
}
