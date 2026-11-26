using Declutter.Core.Health;
using Declutter.Core.Util;

namespace Declutter.Core.Speed;

/// <summary>Honest upgrade advice: what would actually make this PC faster.</summary>
public static class HardwareAdvisor
{
    public static IReadOnlyList<HealthNote> Advise(SystemInfo info, bool systemDriveIsHdd, double? diskUsedFraction, double? typicalBootSeconds)
    {
        var notes = new List<HealthNote>();
        long gb = (info.MemoryBytes ?? 0) >> 30;
        double? used = info.MemoryUsedFraction;

        if (info.MemoryBytes is not null)
        {
            if (gb <= 4)
                notes.Add(new(NoteKind.Critical, $"{gb} GB of memory is very little for Windows 11. Going to 8–16 GB would make the biggest difference of anything here."));
            else if (gb <= 8 && used is >= 0.8)
                notes.Add(new(NoteKind.Warning, $"Your PC is using {used:P0} of its {gb} GB of memory right now. When memory runs out Windows slows to a crawl, so more RAM would help."));
            else if (gb <= 8)
                notes.Add(new(NoteKind.Info, $"{gb} GB of memory is enough for everyday use. If you keep many browser tabs or big apps open, 16 GB is noticeably smoother."));
            else
                notes.Add(new(NoteKind.Good, $"{gb} GB of memory is plenty for everyday use."));

            if (gb <= 8 && info.MemoryModules is int modules && info.MemorySlots is int slots && slots > 0)
                notes.Add(new(NoteKind.Info, modules < slots
                    ? $"There's a free memory slot ({modules} of {slots} used), so adding RAM is usually easy and cheap."
                    : $"All {slots} memory slots are in use, so upgrading means replacing a stick with a bigger one. On many thin laptops memory is soldered and can't be upgraded."));
        }

        if (systemDriveIsHdd)
            notes.Add(new(NoteKind.Warning, "Windows runs from a spinning hard drive. An SSD typically makes start-up and app launches 3–5× faster. It's the best upgrade for an older PC."));
        else if (typicalBootSeconds is > 90)
            notes.Add(new(NoteKind.Info, "Start-up is slow even though you have an SSD, so the culprit is usually startup apps rather than hardware. See the Startup page."));

        if (diskUsedFraction is > 0.9)
            notes.Add(new(NoteKind.Warning, $"Your drive is {diskUsedFraction:P0} full. Windows needs free space to run smoothly and to install updates. Clean up, or consider a bigger drive."));

        return notes;
    }
}
