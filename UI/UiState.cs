using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using StreamLightControl2.Controls;
using StreamLightControl2.Core;
using StreamLightControl2.Core.Models;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2;

/// <summary>Een rij in de wachtrij met live wachttijd.</summary>
public sealed class QueueRow : ObservableObject
{
    public QueueItem Item { get; }
    public int Position { get; }
    private string _wait = "";
    public QueueRow(QueueItem item, int position) { Item = item; Position = position; UpdateWait(); }
    public string Wait { get => _wait; private set => Set(ref _wait, value); }
    public void UpdateWait()
    {
        var s = (DateTime.Now - Item.EnqueuedAt).TotalSeconds;
        Wait = s < 60 ? $"{s:0} s" : $"{(int)(s / 60)} min {(int)(s % 60)} s";
    }
}

/// <summary>
/// Alles wat de interface toont, altijd bijgewerkt op de UI-thread. Pagina's binden hieraan.
/// </summary>
public sealed class UiState : ObservableObject
{
    public AutomationController C { get; }
    public AppSettings Settings => C.Settings;
    public SessionStats Stats => C.Stats;
    public string Version => StreamLightControl2.Core.Services.UpdateService.CurrentVersion.ToString(3);

    public ObservableCollection<HistoryEntry> History { get; } = new();
    public ObservableCollection<HistoryEntry> Recent { get; } = new();
    public ObservableCollection<ErrorEntry> Errors { get; } = new();
    public ObservableCollection<QueueRow> Pending { get; } = new();
    public ObservableCollection<string> RawLog { get; } = new();

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _tick;

    public UiState(AutomationController controller)
    {
        C = controller;
        _ui = Application.Current.Dispatcher;

        foreach (var h in C.History.Snapshot()) History.Add(h);
        foreach (var h in History.Take(8)) Recent.Add(h);
        foreach (var e in C.Errors.Snapshot()) Errors.Add(e);

        C.History.Added += h => Post(() =>
        {
            History.Insert(0, h);
            if (History.Count > HistoryStore.MaxEntries) History.RemoveAt(History.Count - 1);
            Recent.Insert(0, h);
            while (Recent.Count > 8) Recent.RemoveAt(Recent.Count - 1);
        });
        C.History.Cleared += () => Post(() => { History.Clear(); Recent.Clear(); });

        C.Errors.Reported += (e, isNew) => Post(() =>
        {
            if (isNew) Errors.Insert(0, e);
            OnPropertyChanged(nameof(ErrorCount));
            if (isNew && !e.IsWarning) Ui.Toast(e.Title, ToastKind.Error);
        });
        C.Errors.Resolved += key => Post(() =>
        {
            foreach (var e in Errors.Where(x => x.Key == key).ToList()) Errors.Remove(e);
            OnPropertyChanged(nameof(ErrorCount));
        });
        C.Errors.Cleared += () => Post(() => { Errors.Clear(); OnPropertyChanged(nameof(ErrorCount)); });

        C.Queue.Changed += () => Post(RefreshQueue);
        C.Engine.Frame += f => Post(() => ApplyFrame(f));
        C.Engine.Finished += _ => Post(() => { Progress = -1; });
        C.BetterTok.StateChanged += () => Post(RefreshConnection);
        C.DonateWave.StateChanged += () => Post(RefreshDonateWave);
        Settings.DonateWave.PropertyChanged += (_, _) => Post(RefreshDonateWave);
        C.BetterTok.RawMessage += raw => Post(() =>
        {
            if (RawPaused) return;
            RawLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {raw}");
            while (RawLog.Count > 200) RawLog.RemoveAt(RawLog.Count - 1);
        });
        C.AutomationStateChanged += () => Post(RefreshAutomation);
        C.ModeChanged += () => Post(RefreshAutomation);
        C.Spotify.Changed += () => Post(RefreshAutomation);
        C.DevicesStatusChanged += () => Post(RefreshDevices);
        Settings.Devices.CollectionChanged += (_, _) => RefreshDevices();
        Settings.General.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(GeneralOptions.SafeMode)) RefreshAutomation(); };
        C.Store.Saved += (ok, _) => { if (ok) Post(() => SavedPulse?.Invoke()); };

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            foreach (var r in Pending) r.UpdateWait();
            OnPropertyChanged(nameof(LastMessageText));
        };
        _tick.Start();

        RefreshQueue();
        RefreshConnection();
        RefreshAutomation();
        RefreshDevices();
    }

    /// <summary>Wordt afgevuurd nadat instellingen zijn opgeslagen (voor de ‘Opgeslagen’-indicator).</summary>
    public event Action? SavedPulse;

    private void Post(Action a)
    {
        if (_ui.CheckAccess()) a();
        else _ui.BeginInvoke(a);
    }

    // ------------------------------------------------------------ automatisering / LIVE

    private bool _isRunning, _safeMode;
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public bool SafeMode { get => _safeMode; private set => Set(ref _safeMode, value); }
    public bool IsLive => IsSpotifyMode
        ? !SafeMode && C.Spotify.Snapshot.State == PlayerState.Playing
        : IsRunning && !SafeMode && C.BetterTok.State == ConnectionState.Connected;
    public string LiveText => SafeMode ? "SAFE MODE"
        : IsSpotifyMode ? C.Spotify.Snapshot.State switch { PlayerState.Playing => "SPOTIFY", PlayerState.Paused => "SPOTIFY · PAUZE", _ => "SPOTIFY NIET GEOPEND" }
        : !IsRunning ? "AUTOMATISERING UIT" : IsLive ? "LIVE" : "WACHT OP BETTERTOK";
    public Brush LiveBrush => Ui.Res(SafeMode ? "WarningBrush"
        : IsSpotifyMode ? (IsLive ? "SuccessBrush" : "Text3Brush")
        : !IsRunning ? "Text3Brush" : IsLive ? "LiveBrush" : "WarningBrush");

    // ------------------------------------------------------------ modus en Spotify

    public bool IsSpotifyMode => C.Mode == AppMode.Spotify;
    public bool IsStreamMode => !IsSpotifyMode;
    public string ModeText => IsSpotifyMode ? "Spotify + Govee" : "Stream: BetterTok + Govee";
    public string SpotifyTitle => C.Spotify.Snapshot.Track?.Title ?? C.Spotify.Snapshot.State switch
    {
        PlayerState.Paused => "Gepauzeerd",
        PlayerState.NotRunning when !C.Spotify.Running => "Spotify-modus staat uit",
        _ => "Spotify is niet geopend"
    };
    public string SpotifyArtist => C.Spotify.Snapshot.Track?.Artist ?? C.Spotify.Snapshot.State switch
    {
        PlayerState.Paused => "Start een nummer in Spotify",
        PlayerState.NotRunning when !C.Spotify.Running => "Kies Spotify bovenaan in de zijbalk",
        _ => "Open de Spotify-app op deze pc"
    };
    public string SpotifyShort => !C.Spotify.Running ? "Uit" : C.Spotify.Snapshot.State switch
    {
        PlayerState.Playing => "Speelt",
        PlayerState.Paused => "Pauze",
        _ => "Niet geopend"
    };
    public Brush SpotifyBrush => Ui.Res(!C.Spotify.Running ? "Text3Brush" : C.Spotify.Snapshot.State switch
    {
        PlayerState.Playing => "SuccessBrush",
        PlayerState.Paused => "WarningBrush",
        _ => "DangerBrush"
    });
    public IReadOnlyList<string> SpotifyPaletteHex => C.Spotify.Palette;

    private void RefreshSpotify()
    {
        foreach (var n in new[] { nameof(IsSpotifyMode), nameof(IsStreamMode), nameof(ModeText), nameof(SpotifyTitle),
                     nameof(SpotifyArtist), nameof(SpotifyShort), nameof(SpotifyBrush), nameof(SpotifyPaletteHex) })
            OnPropertyChanged(n);
    }
    public string AutomationText => SafeMode ? "Safe Mode – niets wordt afgespeeld" : IsRunning ? "Actief" : "Uit";

    private void RefreshAutomation()
    {
        RefreshSpotify();
        IsRunning = C.IsRunning;
        SafeMode = C.SafeMode;
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(LiveText));
        OnPropertyChanged(nameof(LiveBrush));
        OnPropertyChanged(nameof(AutomationText));
        OnPropertyChanged(nameof(OverallText));
        OnPropertyChanged(nameof(OverallBrush));
    }

    // ------------------------------------------------------------ BetterTok

    public string BetterTokText => C.BetterTok.StatusText;
    public Brush BetterTokBrush => Ui.Res(C.BetterTok.State switch
    {
        ConnectionState.Connected => "SuccessBrush",
        ConnectionState.Connecting or ConnectionState.Reconnecting => "WarningBrush",
        ConnectionState.Failed => "DangerBrush",
        _ => "Text3Brush"
    });
    public bool BetterTokConnected => C.BetterTok.State == ConnectionState.Connected;
    public string BetterTokShort => C.BetterTok.State switch
    {
        ConnectionState.Connected => C.BetterTok.ConnectedMode == BetterTokMode.Cloud ? "Cloud" : "Local",
        ConnectionState.Connecting => "Verbinden…",
        ConnectionState.Reconnecting => "Opnieuw…",
        ConnectionState.Failed => "Fout",
        _ => "Offline"
    };
    public string LastMessageText => C.BetterTok.LastMessageAt is { } t && BetterTokConnected ? "Laatste bericht: " + Ui.Ago(t) : "";

    private void RefreshConnection()
    {
        OnPropertyChanged(nameof(BetterTokText));
        OnPropertyChanged(nameof(BetterTokBrush));
        OnPropertyChanged(nameof(BetterTokConnected));
        OnPropertyChanged(nameof(BetterTokShort));
        OnPropertyChanged(nameof(LastMessageText));
        RefreshAutomation();
    }

    // ------------------------------------------------------------ DonateWave

    /// <summary>Groen = verbonden, oranje = bezig/opnieuw, rood = fout (bijv. sleutel geweigerd), grijs = uit.</summary>
    public Brush DonateWaveBrush => Ui.Res(!Settings.DonateWave.Enabled ? "Text3Brush" : C.DonateWave.State switch
    {
        ConnectionState.Connected => "SuccessBrush",
        ConnectionState.Connecting or ConnectionState.Reconnecting => "WarningBrush",
        ConnectionState.Failed => "DangerBrush",
        _ => "WarningBrush"
    });
    public string DonateWaveShort => !Settings.DonateWave.Enabled ? "Uit" : C.DonateWave.State switch
    {
        ConnectionState.Connected => "Verbonden",
        ConnectionState.Connecting => "Verbinden…",
        ConnectionState.Reconnecting => "Opnieuw…",
        ConnectionState.Failed => "Fout",
        _ => "Offline"
    };
    public string DonateWaveText => "DonateWave-tips: " + (!Settings.DonateWave.Enabled ? "uit" : C.DonateWave.StatusText) + ". Klik voor details.";

    private void RefreshDonateWave()
    {
        OnPropertyChanged(nameof(DonateWaveBrush));
        OnPropertyChanged(nameof(DonateWaveShort));
        OnPropertyChanged(nameof(DonateWaveText));
    }

    // ------------------------------------------------------------ Govee

    public int DevicesOnline => Settings.Devices.Count(d => d.Enabled && d.Online == true);
    public int DevicesActive => Settings.Devices.Count(d => d.Enabled);
    public string GoveeText => Settings.Devices.Count == 0 ? "Geen apparaat"
        : DevicesActive == 0 ? "Geen actief apparaat"
        : Settings.Devices.All(d => d.Online == null) ? $"{DevicesActive} ingesteld"
        : $"{DevicesOnline}/{DevicesActive} online";
    public Brush GoveeBrush => Ui.Res(Settings.Devices.Count == 0 || DevicesActive == 0 ? "DangerBrush"
        : DevicesOnline == DevicesActive ? "SuccessBrush"
        : Settings.Devices.All(d => d.Online == null) ? "Text3Brush" : "WarningBrush");

    public void RefreshDevices()
    {
        OnPropertyChanged(nameof(DevicesOnline));
        OnPropertyChanged(nameof(DevicesActive));
        OnPropertyChanged(nameof(GoveeText));
        OnPropertyChanged(nameof(GoveeBrush));
        OnPropertyChanged(nameof(OverallText));
        OnPropertyChanged(nameof(OverallBrush));
    }

    public string OverallText => SafeMode ? "Safe Mode staat aan. Je instellingen zijn bewaard, er wordt niets afgespeeld."
        : IsSpotifyMode ? (DevicesActive == 0 ? "Spotify-modus: voeg je Govee-lamp toe in Device Manager."
            : C.Spotify.Snapshot.State == PlayerState.Playing ? "Spotify-modus: je lampen bewegen mee met ‘" + C.Spotify.Snapshot.Track!.Label + "’."
            : C.Spotify.Snapshot.State == PlayerState.Paused ? "Spotify-modus: start een nummer in Spotify."
            : "Spotify-modus: open de Spotify-app op deze pc. BetterTok staat uit.")
        : !BetterTokConnected && DevicesActive == 0 ? "Verbind BetterTok en voeg je Govee-lamp toe om te beginnen."
        : !BetterTokConnected ? "BetterTok is nog niet verbonden."
        : DevicesActive == 0 ? "Voeg je Govee-lamp toe in Device Manager."
        : !IsRunning ? "Alles is verbonden. Start de automatisering wanneer je live gaat."
        : "Alles werkt. Events van je LIVE worden automatisch omgezet in licht.";
    public Brush OverallBrush => Ui.Res(SafeMode ? "WarningBrush"
        : IsSpotifyMode ? (DevicesActive > 0 && C.Spotify.Snapshot.State == PlayerState.Playing ? "SuccessBrush" : "WarningBrush")
        : BetterTokConnected && DevicesActive > 0 ? (IsRunning ? "SuccessBrush" : "AccentHoverBrush") : "WarningBrush");

    // ------------------------------------------------------------ wachtrij en afspelen

    private QueueItem? _current;
    public QueueItem? Current { get => _current; private set { if (Set(ref _current, value)) OnPropertyChanged(nameof(IsPlaying)); } }
    public bool IsPlaying => Current != null;
    public bool QueuePaused => C.Queue.Paused;
    public int QueueCount => Pending.Count;

    private void RefreshQueue()
    {
        Current = C.Queue.Current;
        var snapshot = C.Queue.PendingSnapshot();
        Pending.Clear();
        for (var i = 0; i < snapshot.Count; i++) Pending.Add(new QueueRow(snapshot[i], i + 1));
        OnPropertyChanged(nameof(QueueCount));
        OnPropertyChanged(nameof(QueuePaused));
        if (Current == null) Progress = -1;
    }

    private Brush _frameBrush = Brushes.Black;
    private string _frameHex = "#000000";
    private string _frameShowId = "";
    private double _progress = -1;
    private int _frameStep = -1;
    public Brush FrameBrush { get => _frameBrush; private set => Set(ref _frameBrush, value); }
    public string FrameHex { get => _frameHex; private set => Set(ref _frameHex, value); }
    public string FrameShowId { get => _frameShowId; private set => Set(ref _frameShowId, value); }
    public int FrameStep { get => _frameStep; private set => Set(ref _frameStep, value); }
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => Progress < 0 ? 0 : Progress * 100;

    private void ApplyFrame(LightFrame f)
    {
        FrameHex = f.On ? f.ColorHex : "#000000";
        FrameBrush = HexToBrushConverter.Get(FrameHex);
        FrameShowId = f.ShowId;
        FrameStep = f.StepIndex;
        Progress = string.IsNullOrEmpty(f.ShowId) ? -1 : f.Progress;
    }

    // ------------------------------------------------------------ fouten

    public int ErrorCount => Errors.Count;

    private bool _rawPaused;
    public bool RawPaused { get => _rawPaused; set => Set(ref _rawPaused, value); }
}
