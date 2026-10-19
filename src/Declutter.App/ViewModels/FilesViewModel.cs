using System.Collections.ObjectModel;
using System.Text;
using Declutter.App.Mvvm;
using Declutter.Core.Files;
using Declutter.Core.Util;
using Declutter.Windows;
using Microsoft.Win32;

namespace Declutter.App.ViewModels;

public enum FilesTab
{
    Large,
    Old,
    Duplicates,
    Folders,
}

public sealed class FilesViewModel : PageViewModel
{
    private const long DuplicateMinimumBytes = 1024 * 1024;
    private readonly Dictionary<string, FileRowViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private StorageScanResult? _scan;
    private IReadOnlyList<DuplicateGroup> _duplicates = [];
    private string _summary = "Choose where to look, then press Scan. Nothing is deleted until you pick files and confirm.";
    private bool _hasResults;

    public FilesViewModel(AppServices services) : base(services)
    {
        foreach (var folder in services.Folders.PersonalFolders.Where(Directory.Exists))
            Locations.Add(new LocationViewModel(folder, this));
        foreach (var folder in services.Folders.OneDriveFolders.Where(o => !Locations.Any(l => PathUtil.IsSameOrUnder(l.Path, o) || PathUtil.IsSameOrUnder(o, l.Path))))
            Locations.Add(new LocationViewModel(folder, this));

        Tab = new ChoiceSet<FilesTab>(FilesTab.Large,
            (FilesTab.Large, "Large files"), (FilesTab.Old, "Old downloads"), (FilesTab.Duplicates, "Duplicates"), (FilesTab.Folders, "Biggest folders"));
        Tab.Changed += (_, _) => OnPropertiesChanged(nameof(CurrentTab), nameof(IsLargeTab), nameof(IsOldTab), nameof(IsDuplicatesTab), nameof(IsFoldersTab));

        LargeThreshold = new ChoiceSet<long>(100L << 20, (50L << 20, "50 MB+"), (100L << 20, "100 MB+"), (500L << 20, "500 MB+"), (1L << 30, "1 GB+"));
        LargeThreshold.Changed += (_, _) => RebuildLarge();

        OldAge = new ChoiceSet<int>(90, (30, "30+ days"), (90, "3+ months"), (180, "6+ months"), (365, "1+ year"));
        OldAge.Changed += (_, _) => RebuildOld();

        AddFolderCommand = new RelayCommand(AddFolder, () => !IsBusy);
        ScanCommand = new AsyncCommand(ScanAsync, () => !IsBusy && Locations.Count > 0);
        RecycleCommand = new AsyncCommand(RecycleSelectedAsync, () => !IsBusy && SelectedCount > 0);
        SelectExtraCopiesCommand = new RelayCommand(SelectExtraCopies, () => !IsBusy && DuplicateRows.Count > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedCount > 0);
    }

    public override string Title => "Files";
    public override string Glyph => "";

    public ObservableCollection<LocationViewModel> Locations { get; } = [];
    public ChoiceSet<FilesTab> Tab { get; }
    public FilesTab CurrentTab => Tab.Selected;
    public bool IsLargeTab => CurrentTab == FilesTab.Large;
    public bool IsOldTab => CurrentTab == FilesTab.Old;
    public bool IsDuplicatesTab => CurrentTab == FilesTab.Duplicates;
    public bool IsFoldersTab => CurrentTab == FilesTab.Folders;
    public ChoiceSet<long> LargeThreshold { get; }
    public ChoiceSet<int> OldAge { get; }

    public ObservableCollection<FileRowViewModel> LargeFiles { get; } = [];
    public ObservableCollection<FileRowViewModel> OldFiles { get; } = [];

    /// <summary>Group headers and file rows, flattened so the list can virtualise.</summary>
    public ObservableCollection<object> DuplicateRows { get; } = [];
    public ObservableCollection<FolderRowViewModel> Folders { get; } = [];

    public RelayCommand AddFolderCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand RecycleCommand { get; }
    public RelayCommand SelectExtraCopiesCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public bool HasResults
    {
        get => _hasResults;
        private set => Set(ref _hasResults, value);
    }

    public string DownloadsFolder => Services.Folders.Downloads ?? "";

    public int SelectedCount => _rows.Values.Count(r => r.IsSelected);
    public long SelectedBytes => _rows.Values.Where(r => r.IsSelected).Sum(r => r.Entry.Size);
    public string SelectionText => SelectedCount == 0
        ? "No files selected"
        : $"{SelectedCount:N0} file{(SelectedCount == 1 ? "" : "s")} selected · {ByteSize.Format(SelectedBytes)}";

    internal void SelectionChanged() => OnPropertiesChanged(nameof(SelectedCount), nameof(SelectedBytes), nameof(SelectionText));

    internal void RemoveLocation(LocationViewModel location)
    {
        if (!IsBusy) Locations.Remove(location);
    }

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        foreach (var folder in dialog.FolderNames)
        {
            if (Locations.Any(l => PathUtil.PathsEqual(l.Path, folder))) continue;
            Locations.Add(new LocationViewModel(folder, this));
        }
    }

    /// <summary>Scans the given folders (used by the smoke test; the UI uses <see cref="Locations"/>).</summary>
    internal void SetLocations(IEnumerable<string> folders)
    {
        Locations.Clear();
        foreach (var f in folders) Locations.Add(new LocationViewModel(f, this));
    }

    public Task ScanAsync() => RunBusyAsync(async ct =>
    {
        var roots = Locations.Select(l => l.Path).ToList();
        var f = Services.Folders;
        var excluded = new[] { f.WindowsDir, f.ProgramFiles, f.ProgramFilesX86, f.ProgramData, f.AppDataRoot };

        Status = "Scanning…";
        var progress = new Progress<ScanProgress>(p =>
            Status = $"Scanned {p.FilesSeen:N0} files ({ByteSize.Format(p.BytesSeen)})   {p.CurrentFolder}");
        _scan = await Task.Run(() => StorageScanner.Scan(roots, excluded, progress, ct), ct);

        Status = "Looking for duplicates…";
        var dupProgress = new Progress<DuplicateProgress>(p => Status = $"Looking for duplicates… {p.FilesHashed:N0} of {p.FilesToHash:N0} files checked");
        var files = _scan.Files;
        _duplicates = await Task.Run(() => DuplicateFinder.Find(files, DuplicateMinimumBytes, dupProgress, ct), ct);

        _rows.Clear();
        RebuildLarge();
        RebuildOld();
        RebuildDuplicates();
        Folders.Clear();
        long biggest = _scan.Folders.FirstOrDefault()?.Bytes ?? 1;
        foreach (var folder in _scan.Folders.Take(100)) Folders.Add(new FolderRowViewModel(folder, biggest));

        HasResults = true;
        Status = "";
        long wasted = _duplicates.Sum(d => d.WastedBytes);
        Summary = $"Looked at {_scan.Files.Count:N0} files ({ByteSize.Format(_scan.TotalBytes)}). " +
                  (_duplicates.Count > 0 ? $"Duplicates are using {ByteSize.Format(wasted)} of extra space. " : "") +
                  (_scan.CloudOnlySkipped > 0 ? $"{_scan.CloudOnlySkipped:N0} online-only OneDrive files were skipped." : "");
        SelectionChanged();
    });

    private FileRowViewModel Row(FileEntry entry)
    {
        if (!_rows.TryGetValue(entry.Path, out var row))
        {
            row = new FileRowViewModel(entry, this, Services.Folders.OneDriveFolders.Any(o => PathUtil.IsStrictlyUnder(entry.Path, o)));
            _rows[entry.Path] = row;
        }
        return row;
    }

    private void RebuildLarge()
    {
        LargeFiles.Clear();
        if (_scan == null) return;
        foreach (var f in LargeFileFinder.Find(_scan.Files, LargeThreshold.Selected)) LargeFiles.Add(Row(f));
        Tab[FilesTab.Large].Label = $"Large files ({LargeFiles.Count})";
    }

    private void RebuildOld()
    {
        OldFiles.Clear();
        if (_scan == null || Services.Folders.Downloads is not { } downloads) return;
        foreach (var f in OldFileFinder.Find(_scan.Files, downloads, TimeSpan.FromDays(OldAge.Selected), DateTime.UtcNow)) OldFiles.Add(Row(f));
        Tab[FilesTab.Old].Label = $"Old downloads ({OldFiles.Count})";
    }

    private void RebuildDuplicates()
    {
        DuplicateRows.Clear();
        foreach (var group in _duplicates.Take(1000))
        {
            DuplicateRows.Add(new DuplicateHeaderViewModel(group));
            for (int i = 0; i < group.Files.Count; i++)
            {
                var row = Row(group.Files[i]);
                row.IsKeeper = i == 0;
                DuplicateRows.Add(row);
            }
        }
        Tab[FilesTab.Duplicates].Label = $"Duplicates ({_duplicates.Count})";
    }

    private void SelectExtraCopies()
    {
        foreach (var group in _duplicates)
        {
            for (int i = 1; i < group.Files.Count; i++)
            {
                if (_rows.TryGetValue(group.Files[i].Path, out var row)) row.IsSelected = true;
            }
        }
    }

    private void ClearSelection()
    {
        foreach (var row in _rows.Values) row.IsSelected = false;
    }

    private Task RecycleSelectedAsync() => RunBusyAsync(async ct =>
    {
        var selected = _rows.Values.Where(r => r.IsSelected).ToList();
        var paths = selected.Select(r => r.Entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (UserFileDeleter.ValidateDuplicateSelection(_duplicates, paths) is { } problem)
        {
            Services.Dialogs.Show("Keep at least one copy", problem);
            return;
        }

        var message = new StringBuilder($"{selected.Count:N0} files ({ByteSize.Format(selected.Sum(r => r.Entry.Size))}) will be moved to the Recycle Bin. " +
                                        "You can restore them from there if you change your mind.\n\n");
        foreach (var r in selected.Take(12)) message.AppendLine("•  " + r.Name);
        if (selected.Count > 12) message.AppendLine($"…and {selected.Count - 12:N0} more");
        if (selected.Any(r => r.InOneDrive))
            message.AppendLine("\nSome of these are in OneDrive. Deleting them here also deletes them from OneDrive on all your devices.");
        if (!Services.Dialogs.Confirm("Move to Recycle Bin?", message.ToString(), "Move to Recycle Bin", danger: true))
            return;

        var deleter = new UserFileDeleter(Services.Guard, Services.Recycler, Services.Log);
        var progress = new Progress<int>(n => Status = $"Moving files to the Recycle Bin… {n:N0} of {selected.Count:N0}");
        var result = await Services.Recycler.RecycleAsync(deleter, selected.Select(r => r.Entry).ToList(), progress, ct);

        var failed = result.Failures.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in selected.Where(r => !failed.Contains(r.Entry.Path))) Forget(row);
        if (_scan != null)
        {
            var gone = selected.Where(r => !failed.Contains(r.Entry.Path)).Select(r => r.Entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _scan = new StorageScanResult { Files = _scan.Files.Where(f => !gone.Contains(f.Path)).ToList(), Folders = _scan.Folders, CloudOnlySkipped = _scan.CloudOnlySkipped };
            _duplicates = _duplicates
                .Select(g => new DuplicateGroup(g.Hash, g.Size, g.Files.Where(f => !gone.Contains(f.Path)).ToList()))
                .Where(g => g.Files.Count > 1)
                .ToList();
            RebuildDuplicates();
            RebuildLarge();
            RebuildOld();
        }

        Status = "";
        Summary = $"Moved {result.Recycled:N0} files ({ByteSize.Format(result.BytesRecycled)}) to the Recycle Bin. " +
                  "Empty the Recycle Bin in Junk Cleaner to free the space for good.";
        SelectionChanged();
        Services.NotifyDiskChanged();

        if (result.Failures.Count > 0)
        {
            var details = string.Join("\n", result.Failures.Take(10).Select(f => $"•  {Path.GetFileName(f.Path)}: {f.Reason}"));
            Services.Dialogs.Show($"{result.Failures.Count} files weren't moved", details);
        }
    });

    private void Forget(FileRowViewModel row)
    {
        _rows.Remove(row.Entry.Path);
        LargeFiles.Remove(row);
        OldFiles.Remove(row);
    }
}

public sealed class LocationViewModel
{
    public LocationViewModel(string path, FilesViewModel owner)
    {
        Path = path;
        RemoveCommand = new RelayCommand(() => owner.RemoveLocation(this));
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    public RelayCommand RemoveCommand { get; }
}

public sealed class FileRowViewModel : ObservableObject
{
    private readonly FilesViewModel _owner;
    private bool _isSelected;
    private bool _isKeeper;

    public FileRowViewModel(FileEntry entry, FilesViewModel owner, bool inOneDrive)
    {
        Entry = entry;
        _owner = owner;
        InOneDrive = inOneDrive;
        ShowCommand = new RelayCommand(() => SystemTools.ShowInExplorer(entry.Path));
    }

    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string Folder => Entry.Directory;
    public string SizeText => ByteSize.Format(Entry.Size);
    public string DateText => "Modified " + Entry.LastWriteUtc.ToLocalTime().ToString("d MMM yyyy");
    public bool InOneDrive { get; }
    public RelayCommand ShowCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) _owner.SelectionChanged();
        }
    }

    /// <summary>The copy we suggest keeping in a duplicate group.</summary>
    public bool IsKeeper
    {
        get => _isKeeper;
        set => Set(ref _isKeeper, value);
    }
}

public sealed class DuplicateHeaderViewModel(DuplicateGroup group)
{
    public string Title => $"{group.Files.Count} identical copies of \"{group.Files[0].Name}\"";
    public string Detail => $"{ByteSize.Format(group.Size)} each · {ByteSize.Format(group.WastedBytes)} could be freed";
}

public sealed class FolderRowViewModel(FolderSize folder, long biggest)
{
    public string Name => Path.GetFileName(folder.Path) is { Length: > 0 } n ? n : folder.Path;
    public string FullPath => folder.Path;
    public string SizeText => ByteSize.Format(folder.Bytes);
    public string Detail => $"{folder.FileCount:N0} files";
    public double Fraction => biggest <= 0 ? 0 : Math.Max(0.01, (double)folder.Bytes / biggest);
    public RelayCommand OpenCommand { get; } = new(() => SystemTools.ShowInExplorer(folder.Path));
}
