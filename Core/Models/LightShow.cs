using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Serialization;

namespace StreamLightControl2.Core.Models;

public enum StepKind { Color, Off }

/// <summary>Eén stap in een lichtshow.</summary>
public sealed class LightStep : ObservableObject
{
    private StepKind _kind = StepKind.Color;
    private string _colorHex = "#7C6CF6";
    private int _brightness = 100;
    private int _durationMs = 800;
    private int _flashes = 1;
    private int _transitionMs;
    private string _colorHex2 = "";

    public StepKind Kind { get => _kind; set { if (Set(ref _kind, value)) OnPropertyChanged(nameof(Summary)); } }

    public string ColorHex
    {
        get => _colorHex;
        set { if (Set(ref _colorHex, ColorUtil.Normalize(value, _colorHex))) OnPropertyChanged(nameof(Summary)); }
    }

    /// <summary>1–100 %.</summary>
    public int Brightness { get => _brightness; set { if (Set(ref _brightness, Math.Clamp(value, 1, 100))) OnPropertyChanged(nameof(Summary)); } }

    /// <summary>Totale duur van de stap (inclusief flitsen en overgang), 50–60000 ms.</summary>
    public int DurationMs { get => _durationMs; set { if (Set(ref _durationMs, Math.Clamp(value, 50, 60000))) OnPropertyChanged(nameof(Summary)); } }

    /// <summary>1 = vaste kleur, 2+ = zoveel keer knipperen binnen de duur.</summary>
    public int Flashes { get => _flashes; set { if (Set(ref _flashes, Math.Clamp(value, 1, 20))) OnPropertyChanged(nameof(Summary)); } }

    /// <summary>Vloeiende overgang vanaf de vorige kleur (alleen bij 1 flits), 0–10000 ms.</summary>
    public int TransitionMs { get => _transitionMs; set { if (Set(ref _transitionMs, Math.Clamp(value, 0, 10000))) OnPropertyChanged(nameof(Summary)); } }

    /// <summary>Optioneel: kleur voor de tweede helft van de strip (helft/helft-effect). Leeg = hele strip één kleur.</summary>
    public string ColorHex2
    {
        get => _colorHex2;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "" : ColorUtil.Normalize(value, _colorHex2 == "" ? "#4F8DFF" : _colorHex2);
            if (Set(ref _colorHex2, v)) { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(IsSplit)); }
        }
    }

    [JsonIgnore] public bool IsSplit
    {
        get => _colorHex2 != "";
        set { if (value && _colorHex2 == "") ColorHex2 = "#4F8DFF"; else if (!value) ColorHex2 = ""; }
    }

    [JsonIgnore]
    public string Summary => Kind == StepKind.Off
        ? $"Uit · {DurationMs} ms"
        : $"{ColorHex}{(IsSplit ? " | " + ColorHex2 : "")} · {Brightness}% · {DurationMs} ms" +
          (Flashes > 1 ? $" · {Flashes}× flits" : "") +
          (TransitionMs > 0 && Flashes == 1 ? $" · overgang {TransitionMs} ms" : "");

    public LightStep Clone() => new()
    {
        Kind = Kind, ColorHex = ColorHex, Brightness = Brightness,
        DurationMs = DurationMs, Flashes = Flashes, TransitionMs = TransitionMs, ColorHex2 = ColorHex2
    };

    public static LightStep Color(string hex, int durationMs, int brightness = 100, int flashes = 1, int transitionMs = 0) =>
        new() { ColorHex = hex, DurationMs = durationMs, Brightness = brightness, Flashes = flashes, TransitionMs = transitionMs };

    /// <summary>Helft/helft: eerste helft <paramref name="a"/>, tweede helft <paramref name="b"/>.</summary>
    public static LightStep Split(string a, string b, int durationMs, int brightness = 100) =>
        new() { ColorHex = a, ColorHex2 = b, DurationMs = durationMs, Brightness = brightness };

    public static LightStep Off(int durationMs) => new() { Kind = StepKind.Off, DurationMs = durationMs };
}

/// <summary>Een complete lichtshow: stappen + herhalingen.</summary>
public sealed class LightShow : ObservableObject
{
    private string _name = "Nieuwe lichtshow";
    private int _repeats = 1;
    private string _presetKey = "";
    private ObservableCollection<LightStep> _steps = new();

    /// <summary>
    /// Optioneel: twee versies van dezelfde show (stapdata). <see cref="FastData"/> = club-versie voor strips die
    /// direct springen, <see cref="SafeData"/> = rustigere versie voor strips met fade. De app kiest zelf.
    /// </summary>
    public string FastData { get; set; } = "";
    public string SafeData { get; set; } = "";
    [JsonIgnore] public bool HasTiers => FastData != "" && SafeData != "";

    public LightShow() => HookSteps();

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get => _name; set => Set(ref _name, string.IsNullOrWhiteSpace(value) ? "Naamloze show" : value.Trim()); }

    /// <summary>Aantal keer dat alle stappen afgespeeld worden (1–50).</summary>
    public int Repeats { get => _repeats; set { if (Set(ref _repeats, Math.Clamp(value, 1, 50))) OnPropertyChanged(nameof(TotalDurationMs)); } }

    /// <summary>Niet leeg = standaardpreset (kan worden hersteld naar de fabrieksversie).</summary>
    public string PresetKey { get => _presetKey; set { if (Set(ref _presetKey, value ?? "")) { OnPropertyChanged(nameof(IsPreset)); OnPropertyChanged(nameof(PresetBadge)); } } }

    [JsonIgnore] public bool IsPreset => !string.IsNullOrEmpty(PresetKey);
    [JsonIgnore] public string PresetBadge => PresetKey.StartsWith("song-") ? "Song" : "Preset";

    public ObservableCollection<LightStep> Steps
    {
        get => _steps;
        set { _steps.CollectionChanged -= StepsChanged; _steps = value ?? new(); HookSteps(); OnPropertyChanged(); OnPropertyChanged(nameof(TotalDurationMs)); }
    }

    [JsonIgnore] public int TotalDurationMs => Steps.Sum(s => s.DurationMs) * Repeats;

    [JsonIgnore] public string PreviewColors => string.Join(",", Steps.Where(s => s.Kind == StepKind.Color).Select(s => s.ColorHex).Distinct().Take(6));

    private void HookSteps()
    {
        _steps.CollectionChanged += StepsChanged;
        foreach (var s in _steps) s.PropertyChanged += StepPropertyChanged;
    }

    private void StepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (LightStep s in e.OldItems) s.PropertyChanged -= StepPropertyChanged;
        if (e.NewItems != null) foreach (LightStep s in e.NewItems) s.PropertyChanged += StepPropertyChanged;
        OnPropertyChanged(nameof(TotalDurationMs));
        OnPropertyChanged(nameof(PreviewColors));
    }

    private void StepPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TotalDurationMs));
        OnPropertyChanged(nameof(PreviewColors));
    }

    public LightShow Clone(bool newId = false) => new()
    {
        Id = newId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Repeats = Repeats,
        PresetKey = newId ? "" : PresetKey,
        FastData = FastData, SafeData = SafeData,
        Steps = new ObservableCollection<LightStep>(Steps.Select(s => s.Clone()))
    };

    /// <summary>Kopieert inhoud van een andere show naar deze (zelfde Id blijft).</summary>
    public void CopyFrom(LightShow other)
    {
        Name = other.Name;
        Repeats = other.Repeats;
        FastData = other.FastData;
        SafeData = other.SafeData;
        Steps = new ObservableCollection<LightStep>(other.Steps.Select(s => s.Clone()));
    }
}
