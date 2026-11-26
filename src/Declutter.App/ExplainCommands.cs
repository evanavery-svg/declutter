using Declutter.App.Mvvm;
using Declutter.Core.Explain;

namespace Declutter.App;

/// <summary>"What is this?" buttons: opens the Explain window for the row's item.</summary>
public static class ExplainCommands
{
    public static RelayCommand For(Func<ExplainRequest> build) => new(() => Explainer.Shared?.Show(build()));
}
