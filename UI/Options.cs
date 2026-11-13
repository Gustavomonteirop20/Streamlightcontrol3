using StreamLightControl2.Core.Models;

namespace StreamLightControl2;

public sealed record Option(object Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Keuzelijsten voor ComboBoxen.</summary>
public static class Options
{
    public static List<Option> EventTypeList { get; } =
        EventTypes.Triggerable.Select(t => new Option(t, EventTypes.Label(t))).ToList();

    public static List<Option> TipKindList { get; } = new()
    {
        new(TipKinds.Any, TipKinds.Label(TipKinds.Any)),
        new(TipKinds.Shoutout, TipKinds.Label(TipKinds.Shoutout)),
        new(TipKinds.Wheel, TipKinds.Label(TipKinds.Wheel))
    };

    public static List<Option> PriorityList { get; } =
        Enumerable.Range(1, 5).Select(p => new Option(p, Priorities.Label(p))).ToList();

    public static List<Option> FlashModeList { get; } = new()
    {
        new(FlashMode.PowerToggle, "Lamp uit/aan schakelen (meest compatibel)"),
        new(FlashMode.Black, "Kleur naar zwart (sneller knipperen)")
    };

    public static List<Option> RestoreModeList { get; } = new()
    {
        new(RestoreMode.Previous, "Terug naar hoe de lamp was"),
        new(RestoreMode.FixedColor, "Vaste kleur"),
        new(RestoreMode.None, "Laatste kleur van de show laten staan")
    };
}
