using System.IO;
using System.Windows;
using System.Windows.Threading;
using StreamLightControl2.Core;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2;

/// <summary>
/// Bijwerken vanuit de app: bij opstarten automatisch, tijdens gebruik alleen een melding (nooit midden in een LIVE).
/// </summary>
public sealed class Updater : ObservableObject
{
    private readonly UpdateService _service;
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _available;
    private string _status = "";
    private bool _busy;
    private double _progress;

    public Updater(string dataDirectory)
    {
        _service = new UpdateService(dataDirectory);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _timer.Tick += async (_, _) => await CheckAsync(silent: true);
        _timer.Start();
    }

    private static Core.Models.UpdateOptions Options => App.Controller.Settings.Updates;
    public static string InstallDir => Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";

    public UpdateInfo? Available { get => _available; private set { if (Set(ref _available, value)) { OnPropertyChanged(nameof(HasUpdate)); OnPropertyChanged(nameof(BannerText)); } } }
    public bool HasUpdate => Available != null;
    public string BannerText => Available == null ? "" : $"Nieuwe versie {Available.Version.ToString(3)} beschikbaar";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string CurrentVersion => UpdateService.CurrentVersion.ToString(3);
    public bool Configured => !string.IsNullOrWhiteSpace(Options.Repo) && Options.Repo.Contains('/');

    /// <summary>Bij het opstarten: controleren en (als ingesteld) meteen installeren. Geeft true als de app gaat herstarten.</summary>
    public async Task<bool> RunStartupAsync()
    {
        if (!Options.AutoCheck || !Configured) return false;
        await CheckAsync(silent: true);
        if (Available == null || !Options.AutoInstallOnStart) return false;
        if (!UpdateService.CanSelfUpdate(InstallDir).ok) return false;
        Ui.Toast($"Bijwerken naar versie {Available.Version.ToString(3)}…", ToastKind.Info);
        return await InstallAsync(confirm: false);
    }

    public async Task CheckAsync(bool silent)
    {
        if (!Configured)
        {
            Status = "Nog geen update-adres ingesteld.";
            if (!silent) Ui.Toast(Status, ToastKind.Warning);
            return;
        }
        if (Busy) return;
        Busy = true;
        Status = "Controleren…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Available = await _service.CheckAsync(Options.Repo, UpdateService.CurrentVersion, cts.Token);
            Status = Available == null
                ? $"Je hebt de nieuwste versie ({CurrentVersion}). Gecontroleerd om {DateTime.Now:HH:mm}."
                : $"Versie {Available.Version.ToString(3)} is beschikbaar.";
            if (!silent) Ui.Toast(Status, Available == null ? ToastKind.Success : ToastKind.Info);
        }
        catch (Exception ex)
        {
            Status = "Controleren mislukt: " + BetterTokClient.Describe(ex);
            if (!silent) Ui.Toast(Status, ToastKind.Warning);
        }
        finally { Busy = false; }
    }

    /// <summary>Downloadt, controleert en installeert. De app sluit en start opnieuw.</summary>
    public async Task<bool> InstallAsync(bool confirm = true)
    {
        var info = Available;
        if (info == null || Busy) return false;
        var (ok, reason) = UpdateService.CanSelfUpdate(InstallDir);
        if (!ok) { Ui.Toast(reason, ToastKind.Warning); Status = reason; return false; }
        if (confirm)
        {
            var live = App.State.IsLive ? "\n\nLet op: je bent LIVE. De app is ±10 seconden weg tijdens het bijwerken." : "";
            var notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n\nWat is er nieuw:\n" + (info.Notes.Length > 600 ? info.Notes[..600] + "…" : info.Notes);
            if (!await Ui.Confirm($"Bijwerken naar {info.Version.ToString(3)}?",
                    "De app downloadt de nieuwe versie, sluit af en start vanzelf opnieuw. Je instellingen, lichtshows en koppelingen blijven bewaard." + notes + live,
                    "Nu bijwerken")) return false;
        }
        Busy = true;
        try
        {
            Status = "Downloaden…";
            var progress = new Progress<double>(p => { Progress = p * 100; Status = $"Downloaden… {p:P0}"; });
            var files = await _service.DownloadAsync(info, progress);
            Status = "Installeren…";
            App.Controller.Store.SaveNow();
            var script = UpdateService.WriteApplyScript(files, InstallDir, Environment.ProcessId, "StreamLightControl.exe",
                Path.Combine(App.Controller.Store.DataDirectory, "updates"));
            UpdateService.LaunchApplyScript(script);
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            Status = "Bijwerken mislukt: " + ex.Message;
            Ui.Toast(Status, ToastKind.Error);
            return false;
        }
        finally { Busy = false; Progress = 0; }
    }
}
