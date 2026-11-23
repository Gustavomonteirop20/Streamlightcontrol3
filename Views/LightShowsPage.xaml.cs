using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using StreamLightControl2.Controls;
using StreamLightControl2.Core;
using StreamLightControl2.Core.Models;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2.Views;

/// <summary>
/// Light Show Builder. Je bewerkt altijd een werkkopie; ‘Opslaan’ zet hem in je instellingen.
/// ‘Afspelen op lampen’ stuurt de werkkopie via de wachtrij echt naar de Govee-lampen.
/// </summary>
public partial class LightShowsPage : UserControl, IPageActivated
{
    private UiState S => App.State;
    private readonly ListCollectionView _view;
    private LightShow? _original;
    private LightShow? _working;
    private string _savedJson = "";
    private bool _switching;

    public LightShowsPage()
    {
        InitializeComponent();
        _view = new ListCollectionView(S.Settings.Shows);
        ShowsList.ItemsSource = _view;
        Timeline.SetBinding(TimelineView.SelectedIndexProperty, new Binding(nameof(ListBox.SelectedIndex)) { Source = StepsList, Mode = BindingMode.TwoWay });
        S.PropertyChanged += OnStateChanged;
        Timeline.StepClicked += i => { if (i < StepsList.Items.Count) StepsList.ScrollIntoView(StepsList.Items[i]); };
        _playTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playTimer.Tick += (_, _) => AnimatePlayhead();
        Unloaded += (_, _) => _playTimer.Stop();
        if (S.Settings.Shows.Count > 0) ShowsList.SelectedIndex = 0;
        UpdateTargets();
    }

    public void OnActivated() => UpdateTargets();

    private void UpdateTargets()
    {
        var n = App.Controller.Targets.Count;
        TargetsText.Text = n == 0
            ? "Geen actieve lamp. Voeg je lamp toe in Device Manager; tot die tijd zie je alleen de preview."
            : $"Wordt echt verstuurd naar {n} lamp{(n == 1 ? "" : "en")}.";
    }

    // ------------------------------------------------------------ lijst

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        _view.Filter = q.Length == 0 ? null : o => o is LightShow s && s.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private async void ShowsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching) return;
        var next = ShowsList.SelectedItem as LightShow;
        if (ReferenceEquals(next, _original)) return;
        if (IsDirty && _original != null)
        {
            _switching = true;
            ShowsList.SelectedItem = _original;
            _switching = false;
            var save = await Ui.Confirm("Wijzigingen opslaan?", $"Je hebt ‘{_working!.Name}’ aangepast maar nog niet opgeslagen.", "Opslaan");
            if (save) Save(); else _savedJson = Serialize(_working);
            _switching = true;
            ShowsList.SelectedItem = next;
            _switching = false;
        }
        Load(next);
    }

    private void Load(LightShow? show)
    {
        if (_working != null) _working.PropertyChanged -= WorkingChanged;
        _original = show;
        _working = show?.Clone();
        if (_working != null) _working.PropertyChanged += WorkingChanged;
        _savedJson = _working == null ? "" : Serialize(_working);
        Editor.DataContext = _working;
        Editor.IsEnabled = _working != null;
        Timeline.Show = _working;
        _zoom = AutoZoom(_working);
        ApplyZoom();
        TimelineScroller.ScrollToHorizontalOffset(0);
        ResetPresetButton.Visibility = show?.IsPreset == true ? Visibility.Visible : Visibility.Collapsed;
        StepsList.SelectedIndex = _working?.Steps.Count > 0 ? 0 : -1;
        UpdateDirty();
        UpdatePreview();
    }

    private static string Serialize(LightShow s) => JsonSerializer.Serialize(s, SettingsStore.JsonOptions);
    private bool IsDirty => _working != null && Serialize(_working) != _savedJson;

    private void WorkingChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(() => { UpdateDirty(); ApplyZoom(); UpdatePreview(); });

    private void UpdateDirty()
    {
        var dirty = IsDirty;
        DirtyPill.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = dirty;
        RevertButton.IsEnabled = dirty;
    }

    // ------------------------------------------------------------ show-acties

    // ------------------------------------------------------------ importeren / exporteren

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Lichtshow importeren",
            Filter = "Lichtshow (afbeelding of bestand)|*.png;*.json;*.slcshow|Alle bestanden|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var file in dlg.FileNames) await ImportAsync(file);
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var sound in files.Where(SoundDecoder.IsSound)) await MakeFromSoundAsync(sound, ShowGenerator.Palettes[0].Key);
        foreach (var file in files.Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith(".slcshow", StringComparison.OrdinalIgnoreCase)))
            await ImportAsync(file);
    }

    private async Task ImportAsync(string path)
    {
        ShowFile file;
        try { file = ShowFile.Load(System.IO.File.ReadAllBytes(path)); }
        catch (Exception ex)
        {
            Ui.Toast($"‘{System.IO.Path.GetFileName(path)}’ kon niet worden geïmporteerd: {ex.Message}", ToastKind.Error);
            return;
        }
        var settings = S.Settings;
        var existing = file.FindExisting(settings);
        var replace = existing != null && await Ui.Confirm("Bestaande show vervangen?",
            $"Er is al een show ‘{existing.Name}’. Wil je die vervangen door de geïmporteerde versie? Koppelingen aan gifts blijven dan gewoon werken.\n\nKies Annuleren om hem als nieuwe show toe te voegen.",
            "Vervangen");
        var show = file.AddTo(settings, replace);
        App.Controller.Store.RequestSave();
        if (ReferenceEquals(show, _original)) Load(show);
        else ShowsList.SelectedItem = show;
        ShowsList.ScrollIntoView(show);
        Ui.Toast(replace ? $"‘{show.Name}’ is vervangen door de nieuwe versie." : $"‘{show.Name}’ is toegevoegd.", ToastKind.Success);

        // Al gekoppeld (vervangen)? Dan niets vragen.
        if (replace || settings.Triggers.Any(t => t.ShowId == show.Id)) return;
        var answer = await Ui.Prompt("Koppelen aan een gift?",
            "Bij hoeveel coins moet deze show spelen? Gebruik hetzelfde aantal als de dono-sound in BetterTok. Leeg laten = later koppelen in Gift Manager.", "");
        if (string.IsNullOrWhiteSpace(answer)) return;
        if (!int.TryParse(answer.Trim(), out var coins) || coins < 1)
        {
            Ui.Toast("Vul een aantal coins in (bijv. 1500). Je kunt hem ook koppelen in Gift Manager.", ToastKind.Warning);
            return;
        }
        ShowFile.LinkToCoins(settings, show, coins);
        App.Controller.Store.RequestSave();
        Ui.Toast($"Gekoppeld: een gift van {coins} coins speelt nu ‘{show.Name}’.", ToastKind.Success);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_original == null) return;
        var safeName = string.Concat(_original.Name.TrimStart('♪', ' ').Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Lichtshow exporteren",
            FileName = (safeName.Length == 0 ? "lichtshow" : safeName) + " – lichtshow",
            Filter = "Lichtshow-afbeelding (*.png)|*.png|Lichtshow-bestand (*.json)|*.json",
            DefaultExt = ".png"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var file = ShowFile.FromShow(_original);
            var bytes = dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? System.Text.Encoding.UTF8.GetBytes(file.ToJson())
                : ShowFile.EmbedInPng(ShowImage.Render(_original), file.ToJson());
            System.IO.File.WriteAllBytes(dlg.FileName, bytes);
            Ui.Toast("Geëxporteerd. Sleep het bestand in Light Shows (op deze of een andere pc) om het te importeren.", ToastKind.Success);
        }
        catch (Exception ex) { Ui.Toast("Exporteren mislukt: " + ex.Message, ToastKind.Error); }
    }

    // ---------------------------------------------------------------- showmaker: show van een sound

    private void FromSound_Click(object sender, RoutedEventArgs e)
    {
        // Eerst de kleuren kiezen, dan de sound
        var menu = new ContextMenu { PlacementTarget = FromSoundButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var p in ShowGenerator.Palettes)
        {
            var item = new MenuItem { Header = p.Name, Tag = p.Key };
            item.Click += async (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Sound kiezen voor de nieuwe show", Filter = SoundDecoder.Filter };
                if (dlg.ShowDialog() == true) await MakeFromSoundAsync(dlg.FileName, p.Key);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private bool _making;

    private async Task MakeFromSoundAsync(string path, string paletteKey)
    {
        if (_making) return;
        _making = true;
        FromSoundButton.IsEnabled = false;
        var label = FromSoundButton.Content;
        FromSoundButton.Content = "Sound beluisteren…";
        try
        {
            var name = "♪ " + System.IO.Path.GetFileNameWithoutExtension(path);
            var (show, a) = await Task.Run(() =>
            {
                var (mono, rate) = SoundDecoder.Decode(path);
                var analysis = ShowGenerator.Analyze(mono, rate);
                return (ShowGenerator.Generate(analysis, name, paletteKey), analysis);
            });
            show.Name = UniqueName(show.Name);
            // Snelle of veilige versie, net als de andere shows
            if (!ShowTiers.IsFast(S.Settings.Lighting)) show.Steps = Presets.ParseData(show.SafeData);
            S.Settings.Shows.Add(show);
            SearchBox.Text = "";
            ShowsList.SelectedItem = show;
            ShowsList.ScrollIntoView(show);
            var drops = a.Bars.Count(b => b == BarKind.Drop);
            Ui.Toast($"Show gemaakt: {a.Bpm:0} BPM, {a.Kicks.Count} kicks, {drops} drop-maten · {show.TotalDurationMs / 1000.0:0.0} s. Koppel hem bij Event Manager aan een gift of binnenkomst.");
        }
        catch (Exception ex)
        {
            Ui.Toast($"‘{System.IO.Path.GetFileName(path)}’: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            FromSoundButton.Content = label;
            FromSoundButton.IsEnabled = true;
            _making = false;
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var show = new LightShow
        {
            Name = UniqueName("Nieuwe lichtshow"),
            Steps = new ObservableCollection<LightStep> { LightStep.Color("#7B6CF6", 800), LightStep.Color("#4F8DFF", 800, transitionMs: 400) }
        };
        S.Settings.Shows.Add(show);
        SearchBox.Text = "";
        ShowsList.SelectedItem = show;
        NameBox.Focus();
        NameBox.SelectAll();
        Ui.Toast("Nieuwe lichtshow gemaakt");
    }

    private string UniqueName(string baseName)
    {
        var name = baseName;
        for (var i = 2; S.Settings.Shows.Any(s => s.Name == name); i++) name = $"{baseName} {i}";
        return name;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        if (_original == null || _working == null) return;
        if (_working.Steps.Count == 0) { Ui.Toast("Een lichtshow heeft minstens één stap nodig.", ToastKind.Warning); return; }
        _original.CopyFrom(_working);
        _savedJson = Serialize(_working);
        App.Controller.Store.RequestSave();
        UpdateDirty();
        Ui.Toast($"‘{_original.Name}’ opgeslagen");
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        Load(_original);
        Ui.Toast("Wijzigingen weggegooid", ToastKind.Info);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null) return;
        var copy = _working.Clone(newId: true);
        copy.Name = UniqueName(_working.Name + " (kopie)");
        S.Settings.Shows.Add(copy);
        if (IsDirty) _savedJson = Serialize(_working); // kopie bevat de wijzigingen; origineel blijft ongewijzigd
        Load(copy);
        _switching = true;
        ShowsList.SelectedItem = copy;
        _switching = false;
        Ui.Toast($"Gedupliceerd als ‘{copy.Name}’");
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_original == null || _working == null) return;
        var name = await Ui.Prompt("Lichtshow hernoemen", "Nieuwe naam:", _working.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _working.Name = name;
        _original.Name = name;
        _savedJson = Serialize(_original);
        App.Controller.Store.RequestSave();
        UpdateDirty();
        Ui.Toast("Hernoemd");
    }

    private async void ResetPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_original is not { IsPreset: true }) return;
        if (!await Ui.Confirm("Preset herstellen?", $"‘{_original.Name}’ wordt teruggezet naar de standaardversie. Je aanpassingen gaan verloren.", "Herstellen", danger: true)) return;
        _original.CopyFrom(Presets.Create(_original.PresetKey, ShowTiers.IsFast(App.Controller.Settings.Lighting)));
        App.Controller.Store.RequestSave();
        Load(_original);
        Ui.Toast("Preset hersteld");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_original == null) return;
        var used = S.Settings.Triggers.Where(t => t.ShowId == _original.Id).ToList();
        var msg = used.Count == 0
            ? $"‘{_original.Name}’ wordt definitief verwijderd."
            : $"‘{_original.Name}’ wordt gebruikt door {used.Count} koppeling(en): {string.Join(", ", used.Select(t => t.Name))}. Die koppelingen worden uitgezet tot je een andere show kiest.";
        if (!await Ui.Confirm("Lichtshow verwijderen?", msg, "Verwijderen", danger: true)) return;
        foreach (var t in used) { t.ShowId = ""; t.Enabled = false; }
        var index = S.Settings.Shows.IndexOf(_original);
        _savedJson = _working == null ? "" : Serialize(_working); // geen ‘opslaan?’-vraag
        S.Settings.Shows.Remove(_original);
        _original = null;
        ShowsList.SelectedIndex = Math.Min(index, S.Settings.Shows.Count - 1);
        if (S.Settings.Shows.Count == 0) Load(null);
        Ui.Toast("Lichtshow verwijderd");
    }

    // ------------------------------------------------------------ afspelen

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null || _working.Steps.Count == 0) return;
        if (App.Controller.Targets.Count == 0)
        {
            Ui.Toast("Geen actieve lamp gevonden. Voeg je lamp toe in Device Manager.", ToastKind.Warning);
            return;
        }
        App.Controller.PlayShowNow(_working);
        Ui.Toast(IsDirty ? "Niet-opgeslagen versie wordt afgespeeld op je lampen" : "Wordt afgespeeld op je lampen", ToastKind.Info);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => App.Controller.Queue.SkipCurrent();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UiState.FrameHex) or nameof(UiState.Progress) or nameof(UiState.FrameShowId)) UpdatePreview();
    }

    private void UpdatePreview()
    {
        var playingThis = _working != null && S.FrameShowId == _working.Id && S.Progress >= 0;
        string hex;
        if (playingThis)
        {
            hex = S.FrameHex;
            PreviewLabel.Text = $"Speelt af op je lampen · stap {S.FrameStep + 1}";
            // Engine meldt de voortgang per stap; daartussen schuift de afspeelkop zelf vloeiend door
            if (Math.Abs(S.Progress - _frameProgress) > 0.0001) { _frameProgress = S.Progress; _frameAt = DateTime.Now; }
            if (!_playTimer.IsEnabled) _playTimer.Start();
            AnimatePlayhead();
            PlayButton.Content = "Opnieuw afspelen";
        }
        else
        {
            var step = StepsList.SelectedItem as LightStep ?? _working?.Steps.FirstOrDefault();
            hex = step == null || step.Kind == StepKind.Off ? "#000000" : step.ColorHex;
            PreviewLabel.Text = step == null ? "Preview" : $"Preview · stap {(_working?.Steps.IndexOf(step) ?? 0) + 1}";
            _playTimer.Stop();
            _frameProgress = -1;
            Timeline.Progress = -1;
            PlayButton.Content = "Afspelen op lampen";
        }
        PreviewSwatch.Background = HexToBrushConverter.Get(hex);
        if (PreviewSwatch.Effect is DropShadowEffect glow && ColorUtil.TryParseHex(hex, out var r, out var g, out var b))
        {
            glow.Color = Color.FromRgb(r, g, b);
            glow.Opacity = hex == "#000000" ? 0 : 0.55;
        }
        Timeline.InvalidateVisual();
    }

    // ------------------------------------------------------------ tijdlijn: zoomen, scrollen, afspeelkop

    private readonly System.Windows.Threading.DispatcherTimer _playTimer;
    private double _zoom = 1;
    private double _frameProgress = -1;
    private DateTime _frameAt;

    /// <summary>Lange shows met veel stappen meteen ingezoomd, zodat elk blok aan te klikken is.</summary>
    private static double AutoZoom(LightShow? show)
    {
        if (show == null || show.Steps.Count <= 40) return 1;
        return Math.Clamp(Math.Round(show.Steps.Count / 40.0), 1, 8);
    }

    private void ApplyZoom()
    {
        var view = TimelineScroller.ViewportWidth > 0 ? TimelineScroller.ViewportWidth : TimelineScroller.ActualWidth;
        if (view <= 0) return;
        Timeline.Width = Math.Max(60, Math.Floor(view * _zoom));
        ZoomText.Text = $"{_zoom:0.#}×";
        var show = _working;
        TimelineInfo.Text = show == null || show.Steps.Count == 0 ? ""
            : $"{show.Steps.Count} stappen · {show.TotalDurationMs / 1000.0:0.0} s · klik op een blok om het te bewerken";
        Timeline.InvalidateVisual();
    }

    private void SetZoom(double zoom, double? anchorX = null)
    {
        zoom = Math.Clamp(zoom, 1, 16);
        if (Math.Abs(zoom - _zoom) < 0.001) return;
        // het punt onder de muis (of het midden) blijft op zijn plek
        var anchor = anchorX ?? TimelineScroller.ViewportWidth / 2;
        var frac = (TimelineScroller.HorizontalOffset + anchor) / Math.Max(1, Timeline.Width);
        _zoom = zoom;
        ApplyZoom();
        TimelineScroller.UpdateLayout();
        TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, frac * Timeline.Width - anchor));
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.5);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.5);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => SetZoom(1);
    private void TimelineScroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyZoom();

    private void Timeline_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var mods = System.Windows.Input.Keyboard.Modifiers;
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            SetZoom(e.Delta > 0 ? _zoom * 1.25 : _zoom / 1.25, e.GetPosition(TimelineScroller).X);
            e.Handled = true;
        }
        else if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift) && _zoom > 1)
        {
            TimelineScroller.ScrollToHorizontalOffset(TimelineScroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
        else
        {
            // gewoon scrollen = de pagina scrollen (de tijdlijn zelf scrollt niet verticaal)
            e.Handled = true;
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(TimelineScroller) as UIElement;
            parent?.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
        }
    }

    /// <summary>Zorgt dat stap <paramref name="index"/> zichtbaar is in de tijdlijn.</summary>
    private void ScrollTimelineTo(int index)
    {
        if (index < 0 || _zoom <= 1) return;
        var (left, width) = Timeline.StepBounds(index);
        var view = TimelineScroller.ViewportWidth;
        var off = TimelineScroller.HorizontalOffset;
        if (left < off || left + width > off + view)
            TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, left - view / 3));
    }

    private void AnimatePlayhead()
    {
        var show = _working;
        if (show == null || _frameProgress < 0) { Timeline.Progress = -1; return; }
        var total = Math.Max(1, show.TotalDurationMs);
        var p = Math.Min(1, _frameProgress + (DateTime.Now - _frameAt).TotalMilliseconds / total);
        Timeline.Progress = p;
        // ingezoomd: de afspeelkop in beeld houden
        var x = Timeline.PlayheadX;
        if (x >= 0 && _zoom > 1)
        {
            var view = TimelineScroller.ViewportWidth;
            var off = TimelineScroller.HorizontalOffset;
            if (x < off || x > off + view * 0.85) TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, x - view * 0.15));
        }
    }

    // ------------------------------------------------------------ stappen

    private void StepsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var step = StepsList.SelectedItem as LightStep;
        Inspector.DataContext = step;
        Inspector.Visibility = step == null ? Visibility.Collapsed : Visibility.Visible;
        NoStepText.Visibility = step == null ? Visibility.Visible : Visibility.Collapsed;
        if (step != null)
        {
            StepTitle.Text = $"Stap {StepsList.SelectedIndex + 1}";
            step.PropertyChanged -= SelectedStepChanged;
            step.PropertyChanged += SelectedStepChanged;
            UpdateKindSection(step);
        }
        foreach (var old in e.RemovedItems.OfType<LightStep>()) old.PropertyChanged -= SelectedStepChanged;
        ScrollTimelineTo(StepsList.SelectedIndex);
        Timeline.InvalidateVisual();
        UpdatePreview();
    }

    private void SelectedStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is LightStep s && e.PropertyName == nameof(LightStep.Kind)) UpdateKindSection(s);
    }

    private void UpdateKindSection(LightStep s) =>
        ColorSection.Visibility = s.Kind == StepKind.Off ? Visibility.Collapsed : Visibility.Visible;

    private void AddColor_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null) return;
        var last = _working.Steps.LastOrDefault(s => s.Kind == StepKind.Color);
        var step = LightStep.Color(last?.ColorHex ?? "#7B6CF6", last?.DurationMs ?? 800, last?.Brightness ?? 100);
        InsertStep(step);
    }

    private void AddOff_Click(object sender, RoutedEventArgs e) => InsertStep(LightStep.Off(300));

    private void InsertStep(LightStep step)
    {
        if (_working == null) return;
        var at = StepsList.SelectedIndex >= 0 ? StepsList.SelectedIndex + 1 : _working.Steps.Count;
        _working.Steps.Insert(at, step);
        StepsList.SelectedIndex = at;
        StepsList.ScrollIntoView(step);
    }

    private static LightStep? StepOf(object sender) => (sender as Button)?.CommandParameter as LightStep;

    private void StepUp_Click(object sender, RoutedEventArgs e) => MoveStep(StepOf(sender), -1);
    private void StepDown_Click(object sender, RoutedEventArgs e) => MoveStep(StepOf(sender), 1);

    private void MoveStep(LightStep? step, int dir)
    {
        if (_working == null || step == null) return;
        var i = _working.Steps.IndexOf(step);
        var j = i + dir;
        if (i < 0 || j < 0 || j >= _working.Steps.Count) return;
        _working.Steps.Move(i, j);
        StepsList.SelectedIndex = j;
    }

    private void StepCopy_Click(object sender, RoutedEventArgs e)
    {
        var step = StepOf(sender);
        if (_working == null || step == null) return;
        var i = _working.Steps.IndexOf(step);
        _working.Steps.Insert(i + 1, step.Clone());
        StepsList.SelectedIndex = i + 1;
    }

    private void StepDelete_Click(object sender, RoutedEventArgs e)
    {
        var step = StepOf(sender);
        if (_working == null || step == null) return;
        var i = _working.Steps.IndexOf(step);
        _working.Steps.Remove(step);
        StepsList.SelectedIndex = Math.Min(i, _working.Steps.Count - 1);
    }

    private async void TestStep_Click(object sender, RoutedEventArgs e)
    {
        if (Inspector.DataContext is not LightStep step) return;
        if (step.Kind == StepKind.Off) await Ui.Busy(sender, "Bezig…", () => App.Controller.QuickPowerAsync(false));
        else await Ui.Busy(sender, "Bezig…", () => App.Controller.QuickColorAsync(step.ColorHex, step.Brightness));
    }
}
