using Declutter.App.Mvvm;
using Declutter.Core.Apps;
using Declutter.Core.Startup;

namespace Declutter.App.ViewModels;

/// <summary>How recommendations are labelled and coloured in the UI.</summary>
public static class Advice
{
    public static (string Label, Tone Tone) For(StartupAdvice advice) => advice switch
    {
        StartupAdvice.Keep => ("Keep on", Tone.Good),
        StartupAdvice.Optional => ("Your choice", Tone.Info),
        StartupAdvice.SafeToDisable => ("Safe to turn off", Tone.Caution),
        _ => ("Not sure", Tone.Neutral),
    };

    public static (string Label, Tone Tone) For(AppAdvice advice) => advice switch
    {
        AppAdvice.Required => ("Needed", Tone.Danger),
        AppAdvice.Useful => ("Useful", Tone.Good),
        AppAdvice.Bloatware => ("Suggested to remove", Tone.Caution),
        _ => ("", Tone.Neutral),
    };
}
