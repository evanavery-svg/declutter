using System.Windows.Input;
using Declutter.App.Mvvm;

namespace Declutter.App.ViewModels;

public abstract class PageViewModel(AppServices services) : ObservableObject
{
    private bool _isBusy;
    private string _status = "";
    private bool _visited;
    private CancellationTokenSource? _cts;

    protected AppServices Services { get; } = services;

    public abstract string Title { get; }
    public abstract string Glyph { get; }

    public bool IsElevated => Services.IsElevated;

    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Status
    {
        get => _status;
        protected set => Set(ref _status, value);
    }

    private RelayCommand? _cancel;
    public RelayCommand CancelCommand => _cancel ??= new(() => _cts?.Cancel(), () => IsBusy);

    /// <summary>Called each time the page is shown; the first visit can kick off a read-only scan.</summary>
    public Task NavigatedToAsync()
    {
        if (_visited) return Task.CompletedTask;
        _visited = true;
        return OnFirstVisitAsync();
    }

    protected virtual Task OnFirstVisitAsync() => Task.CompletedTask;

    /// <summary>Runs one long operation with busy state and cancellation.</summary>
    protected async Task RunBusyAsync(Func<CancellationToken, Task> work)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One option in a row of pill buttons.</summary>
public sealed class Choice<T>(ChoiceSet<T> owner, T value, string label) : ObservableObject
{
    private string _label = label;

    public T Value { get; } = value;

    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public bool IsSelected
    {
        get => EqualityComparer<T>.Default.Equals(owner.Selected, Value);
        set
        {
            if (value) owner.Selected = Value;
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

public sealed class ChoiceSet<T> : ObservableObject
{
    private T _selected;

    public ChoiceSet(T initial, params (T Value, string Label)[] options)
    {
        _selected = initial;
        Options = options.Select(o => new Choice<T>(this, o.Value, o.Label)).ToList();
    }

    public IReadOnlyList<Choice<T>> Options { get; }

    /// <summary>A unique radio group name for this set's pills.</summary>
    public string Group { get; } = "g" + Guid.NewGuid().ToString("N");

    public event EventHandler? Changed;

    public T Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var o in Options) o.Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public Choice<T> this[T value] => Options.First(o => EqualityComparer<T>.Default.Equals(o.Value, value));
}
