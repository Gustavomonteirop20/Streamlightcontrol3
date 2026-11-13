using System.Windows;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2;

/// <summary>Snelle acties (dashboard en Stream Mode). Alles gaat echt naar de lampen.</summary>
public static class QuickActions
{
    public static async void Run(object sender, string action)
    {
        var c = App.Controller;
        switch (action)
        {
            case "on": await Ui.Busy(sender, "…", () => c.QuickPowerAsync(true), "Lampen aan"); break;
            case "off": await Ui.Busy(sender, "…", () => c.QuickPowerAsync(false), "Lampen uit"); break;
            case "red": await Ui.Busy(sender, "…", () => c.QuickColorAsync("#FF1A1A")); break;
            case "green": await Ui.Busy(sender, "…", () => c.QuickColorAsync("#1AFF4A")); break;
            case "blue": await Ui.Busy(sender, "…", () => c.QuickColorAsync("#1A4DFF")); break;
            case "white": await Ui.Busy(sender, "…", () => c.QuickColorAsync("#FFFFFF")); break;
            case "rainbow":
                var show = c.Settings.Shows.FirstOrDefault(s => s.PresetKey == Presets.Rainbow) ?? Presets.Create(Presets.Rainbow);
                if (c.Targets.Count == 0) { Ui.Toast("Voeg eerst je Govee-lamp toe in Device Manager.", ToastKind.Warning); return; }
                c.PlayShowNow(show);
                Ui.Toast("Rainbow wordt afgespeeld", ToastKind.Info);
                break;
            case "stop":
                c.StopAllEffects();
                Ui.Toast("Alle effecten gestopt en wachtrij leeggemaakt", ToastKind.Info);
                break;
        }
    }
}
