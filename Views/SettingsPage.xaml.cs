using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StreamLightControl2.Core.Models;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2.Views;

public partial class SettingsPage : UserControl
{
    private AutomationController C => App.Controller;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = App.State;
        DataPath.Text = C.Store.DataDirectory;
        UpdateRestore();
        InitThemes();
        CurrentVersionRun.Text = App.Updater.CurrentVersion;
        App.Updater.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshUpdate);
        RefreshUpdate();
    }

    // ------------------------------------------------------------ vertraging meten

    private Stopwatch? _measureClock;
    private readonly List<long> _taps = new();

    private async void MeasureLatency_Click(object sender, RoutedEventArgs e)
    {
        if (_measureClock != null) return;
        if (C.Targets.Count == 0) { Ui.Toast("Voeg eerst je Govee-lamp toe in Device Manager.", ToastKind.Warning); return; }
        lock (_taps) _taps.Clear();
        _measureClock = Stopwatch.StartNew();
        MeasureButton.IsEnabled = false;
        MeasurePanel.Visibility = Visibility.Visible;
        MeasureResult.Text = " ";
        MeasureProgress.Text = "Maak je klaar…";
        TapButton.Focus();
        PreviewKeyDown += Measure_KeyDown;
        try
        {
            var clock = _measureClock;
            var result = await C.MeasureLatencyAsync(() => clock.ElapsedMilliseconds, _taps, 12,
                new Progress<int>(n => MeasureProgress.Text = $"Flits {n} van 12"));
            if (result is int ms)
            {
                C.Settings.Lighting.LatencyMs = ms;
                MeasureResult.Text = ms == 0
                    ? "Gemeten: je strip loopt niet merkbaar achter. ‘Licht eerder sturen’ staat op 0 ms."
                    : $"Gemeten: je strip loopt ±{ms} ms achter. Het licht wordt nu {ms} ms eerder gestuurd, zodat het precies op de beat valt.";
                Ui.Toast($"Vertraging gemeten: {ms} ms – ingesteld", ToastKind.Success);
            }
            else MeasureResult.Text = "Te weinig tikken op de flitsen. Probeer het nog eens en tik op elke flits mee.";
        }
        catch (Exception ex) { MeasureResult.Text = "Meten mislukt: " + ex.Message; }
        finally
        {
            PreviewKeyDown -= Measure_KeyDown;
            _measureClock = null;
            MeasureButton.IsEnabled = true;
            MeasurePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Tap()
    {
        if (_measureClock is { } c) lock (_taps) _taps.Add(c.ElapsedMilliseconds);
    }

    private void Tap_Click(object sender, RoutedEventArgs e) => Tap();

    private void Measure_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Space || e.IsRepeat) return;
        Tap();
        e.Handled = true;   // spatie mag de Tik-knop niet nóg een keer laten klikken
    }

    private void RefreshUpdate()
    {
        UpdateStatus.Text = string.IsNullOrEmpty(App.Updater.Status)
            ? (App.Updater.Configured ? "Nog niet gecontroleerd." : "Vul het update-adres in om updates te krijgen.")
            : App.Updater.Status;
        InstallUpdateButton.Visibility = App.Updater.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Content = App.Updater.HasUpdate ? $"Bijwerken naar {App.Updater.Available!.Version.ToString(3)}" : "Bijwerken";
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await Ui.Busy(sender, "Controleren…", () => App.Updater.CheckAsync(silent: false));

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) =>
        await Ui.Busy(sender, "Bezig…", () => App.Updater.InstallAsync());

    private void Restore_Changed(object sender, SelectionChangedEventArgs e) => UpdateRestore();

    private void UpdateRestore()
    {
        if (FixedColorPanel == null) return;
        FixedColorPanel.Visibility = C.Settings.Lighting.RestoreMode == RestoreMode.FixedColor ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SafeMode_Click(object sender, RoutedEventArgs e)
    {
        // De schakelaar volgt UiState.SafeMode via de binding; hier alleen omzetten.
        if (Ui.Window != null) await Ui.Window.ToggleSafeModeAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{C.Store.DataDirectory}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Ui.Toast("Map openen mislukt: " + ex.Message, ToastKind.Error); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Stream Light Control back-up (*.json)|*.json", FileName = $"streamlightcontrol-backup-{DateTime.Now:yyyy-MM-dd}.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            C.Store.ExportTo(dialog.FileName);
            Ui.Toast("Back-up opgeslagen: " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { Ui.Toast("Back-up maken mislukt: " + ex.Message, ToastKind.Error); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Stream Light Control back-up (*.json)|*.json|Alle bestanden|*.*" };
        if (dialog.ShowDialog() != true) return;
        AppSettings restored;
        try { restored = SettingsStore.ValidateBackup(dialog.FileName); }
        catch (Exception ex) { Ui.Toast("Dit is geen geldige back-up: " + ex.Message, ToastKind.Error); return; }
        if (!await Ui.Confirm("Back-up terugzetten?",
                $"Deze back-up bevat {restored.Shows.Count} lichtshows, {restored.Triggers.Count} koppelingen en {restored.Devices.Count} apparaten. Je huidige instellingen worden vervangen en de app start opnieuw.",
                "Terugzetten en herstarten", danger: true)) return;
        try
        {
            AutoBackup();
            C.Store.Dispose();
            C.Store.ReplaceFileWith(restored);
            Restart();
        }
        catch (Exception ex) { Ui.Toast("Terugzetten mislukt: " + ex.Message, ToastKind.Error); }
    }

    private async void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        if (!await Ui.Confirm("Statistieken resetten?", "De tellers op het dashboard gaan terug naar 0. Event History blijft bewaard.", "Resetten")) return;
        C.Stats.Reset();
        C.Rules.ResetSession();
        Ui.Toast("Statistieken gereset");
    }

    private async void FactoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (!await Ui.Confirm("Alles resetten?", "Al je lichtshows, koppelingen, apparaten en instellingen worden verwijderd. Er wordt eerst een back-up gemaakt in de opslagmap. De app start daarna opnieuw.", "Alles resetten", danger: true)) return;
        try
        {
            AutoBackup();
            C.Store.Dispose();
            File.Delete(C.Store.SettingsPath);
            Restart();
        }
        catch (Exception ex) { Ui.Toast("Resetten mislukt: " + ex.Message, ToastKind.Error); }
    }

    private void AutoBackup()
    {
        var path = Path.Combine(C.Store.DataDirectory, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        C.Store.ExportTo(path);
    }

    // ---------------------------------------------------------------- thema

    private sealed record ThemeChoice(string Key, string Name);
    private bool _themeReady;

    private void InitThemes()
    {
        ThemeBox.ItemsSource = AppThemes.Choices.Select(c => new ThemeChoice(c.Key, c.Name)).ToList();
        ThemeBox.SelectedValue = AppThemes.Choices.Any(c => c.Key == C.Settings.General.Theme) ? C.Settings.General.Theme : "standaard";
        _themeReady = true;
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeReady || ThemeBox.SelectedValue is not string key) return;
        C.Settings.General.Theme = key;
        var willBe = AppThemes.Resolve(key, DateTime.Today);
        var changes = willBe.Key != ThemeApplier.Current.Key;
        ThemeRestartButton.Visibility = changes ? Visibility.Visible : Visibility.Collapsed;
        ThemeHint.Text = changes
            ? $"Opgeslagen: {willBe.Name}. Klik op Herstarten om het nieuwe thema te zien (een lopende show stopt dan)."
            : "Kerst, herfst of zomer: andere kleuren voor de hele app. ‘Automatisch’ kiest zelf het seizoen.";
    }

    private void ThemeRestart_Click(object sender, RoutedEventArgs e)
    {
        C.Store.SaveNow();
        Restart();
    }

    private static void Restart()
    {
        var exe = Environment.ProcessPath;
        App.Controller.Dispose();
        if (exe != null) Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
