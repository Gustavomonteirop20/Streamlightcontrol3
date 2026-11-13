using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// Shows met twee versies: club (snel, voor strips die direct springen) en veilig (voor strips met fade).
/// Welke speelt hangt af van de snelheidslimiet: 400 ms of sneller = club-versie.
/// </summary>
public static class ShowTiers
{
    public const int FastThresholdMs = 400;

    public static bool IsFast(LightingOptions o) => o.MinChangeMs <= FastThresholdMs;

    /// <summary>De twee versies van een show (null als hij er maar één heeft).</summary>
    public static (string fast, string safe)? VersionsOf(LightShow show)
    {
        if (show.HasTiers) return (show.FastData, show.SafeData);
        if (show.IsPreset && Presets.FastKeys.Contains(show.PresetKey))
            return (Presets.ToData(Presets.Create(show.PresetKey, true).Steps), Presets.ToData(Presets.Create(show.PresetKey, false).Steps));
        return null;
    }

    /// <summary>
    /// Zet elke show met twee versies op de versie die past bij de strip. Een show die je zelf hebt aangepast
    /// (dus geen van beide versies meer is) blijft zoals hij is. Geeft het aantal gewisselde shows.
    /// </summary>
    public static int Apply(AppSettings settings)
    {
        var fast = IsFast(settings.Lighting);
        var changed = 0;
        foreach (var show in settings.Shows)
        {
            if (VersionsOf(show) is not { } v) continue;
            var target = Normalize(fast ? v.fast : v.safe);
            var other = Normalize(fast ? v.safe : v.fast);
            var current = Presets.ToData(show.Steps);
            if (current == target) continue;
            if (current != other) continue;               // zelf aangepast: niet aankomen
            var fresh = Presets.ParseData(target);
            if (show.IsPreset && !show.HasTiers)
            {
                var p = Presets.Create(show.PresetKey, fast);
                show.Repeats = p.Repeats;
                fresh = p.Steps;
            }
            show.Steps = fresh;
            changed++;
        }
        return changed;
    }

    /// <summary>Zelfde tekst als na inlezen en weer opslaan (zodat vergelijken altijd klopt).</summary>
    private static string Normalize(string data) => Presets.ToData(Presets.ParseData(data));
}
