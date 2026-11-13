using System.Collections.Specialized;
using System.ComponentModel;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// Het hart van de app: BetterTok-event → regels → wachtrij → light show engine → Govee LAN.
/// Bevat geen WPF-code, zodat alles los te testen is.
/// </summary>
public sealed class AutomationController : IDisposable
{
    public SettingsStore Store { get; }
    public AppSettings Settings => Store.Settings;
    public GoveeLanClient Govee { get; }
    public LightShowEngine Engine { get; }
    public AutomationQueue Queue { get; }
    public BetterTokClient BetterTok { get; }
    public DonateWaveClient DonateWave { get; }
    public SpotifyLightController Spotify { get; }
    public RuleEngine Rules { get; } = new();
    public ErrorCenter Errors { get; }
    public HistoryStore History { get; }
    public SessionStats Stats { get; } = new();

    /// <summary>Voert iets uit op de UI-thread (null = direct, gebruikt in tests).</summary>
    public Action<Action>? Dispatch { get; set; }

    public bool IsRunning { get; private set; }
    public bool SafeMode => Settings.General.SafeMode;
    public LightFrame? LastFrame { get; private set; }

    public event Action? AutomationStateChanged;
    public event Action<HistoryEntry>? EventHandled;
    public event Action<LightFrame>? FrameChanged;
    public event Action? DevicesStatusChanged;

    private volatile string[] _targets = Array.Empty<string>();
    private Timer? _pollTimer;
    private int _polling;

    public AutomationController(string? dataDirectory = null, GoveeLanClient? govee = null,
        INowPlayingSource? nowPlaying = null, Func<IAudioLevelSource?>? audio = null, System.Net.Http.HttpMessageHandler? donateWaveHttp = null)
    {
        Store = new SettingsStore(dataDirectory);
        Errors = new ErrorCenter(Store.DataDirectory);   // app.log in de instellingenmap
        History = new HistoryStore(Store.DataDirectory);
        Govee = govee ?? new GoveeLanClient();
        Engine = new LightShowEngine(Govee, () => Settings.Lighting);
        Engine.Zones = ip =>
        {
            var d = Settings.Devices.FirstOrDefault(x => x.Ip == ip && x.Enabled);
            if (d == null || d.VisibleZones.Length == 0) return null;
            var segment = d.ZoneMethod == "segment";
            var count = segment ? Math.Min(d.ZoneCount, 16) : d.ZoneCount;
            var vis = GoveeDeviceConfig.ParseZones(d.VisibleZones, count).ToList();
            return vis.Count == 0 ? null : new ZoneSetup(count, vis, segment);
        };
        Queue = new AutomationQueue(Engine, () => _targets, () => Settings.Queue);
        BetterTok = new BetterTokClient(Errors);
        DonateWave = new DonateWaveClient(Errors, donateWaveHttp);
        Donations = new DonationStats(Store.DataDirectory);
        _overlayCert = new Lazy<OverlayCert?>(() =>
        {
            try { return OverlayCert.LoadOrCreate(Store.DataDirectory); }
            catch (Exception ex) { Errors.WriteLog($"[Overlays] certificaat: {ex.Message}"); return null; }
        });
        Overlays = new OverlayServer(Donations, () => Settings.Overlays, Errors.WriteLog, () => _overlayCert.Value?.Leaf);
        Spotify = new SpotifyLightController(Engine, () => _targets, () => Settings, () => Queue.Current != null || Calibrating, Errors, nowPlaying, audio);
    }

    public AppMode Mode => Settings.General.Mode;

    /// <summary>Bijgehouden DonateWave-tips voor de kerst-overlays.</summary>
    public DonationStats Donations { get; }
    /// <summary>Lokale bron (127.0.0.1) voor de kerst-overlays in OBS / TikTok LIVE Studio.</summary>
    public OverlayServer Overlays { get; }

    private readonly Lazy<OverlayCert?> _overlayCert;
    /// <summary>Eigen certificaat voor https://kerst.slc-overlay.nl (TikTok LIVE Studio).</summary>
    public OverlayCert? OverlayCertificate => _overlayCert.Value;

    /// <summary>Tipdoel opnieuw vanaf € 0 laten tellen.</summary>
    public void ResetTipGoal()
    {
        Settings.Overlays.GoalStartUtc = DateTime.UtcNow;
        Store.RequestSave();
    }

    /// <summary>Moment waarop de DonateWave-overlay de tip toont (voor de kerst-alert).</summary>
    private DateTime TipAlertMoment(StreamEvent e, DateTime? soundStart)
    {
        if (soundStart is { } s) return s;
        var occ = e.OccurredAtUtc ?? DateTime.UtcNow;
        if (e.TipKind == TipKinds.Shoutout)
        {
            // Zonder eigen regel: gemeten tijden met de animatieduur van de special met dit bedrag (anders 7 s)
            var anim = Settings.Triggers.FirstOrDefault(t => t.EventType == EventTypes.Tip && t.TipKind == TipKinds.Shoutout && t.StartDelayMs > 0 && t.CoinsMatch(e.CoinsPerItem))?.StartDelayMs ?? 7000;
            return occ.AddMilliseconds(Settings.DonateWave.OverlayDelayMs + DwArriveToAnimMs + anim + DwAnimEndToSoundMs);
        }
        return occ.AddMilliseconds(Settings.DonateWave.OverlayDelayMs + DwArriveToSoundMs);
    }

    /// <summary>Vertraging meten loopt: Spotify-licht en wachtrij blijven even van de lampen af.</summary>
    public bool Calibrating { get; private set; }

    /// <summary>
    /// Vertraging meten. De strip flitst wit in een vast ritme (elke 1,2 s); de gebruiker tikt (via <paramref name="taps"/>)
    /// op het moment dat hij de flits ZIET. Resultaat = hoeveel ms het licht eerder moet worden verstuurd.
    /// </summary>
    public async Task<int?> MeasureLatencyAsync(Func<long> clock, List<long> taps, int flashes = 12, IProgress<int>? progress = null, CancellationToken token = default)
    {
        if (_targets.Length == 0) throw new InvalidOperationException("Er is nog geen actief Govee-apparaat. Voeg je lamp toe in Device Manager.");
        var queueWasPaused = Queue.Paused;
        Calibrating = true;
        Queue.StopCurrent();
        var sent = new List<long>();
        try
        {
            await Task.Delay(300, token);
            await Engine.LiveColorAsync(_targets, "#000000", 100, "Vertraging meten", token);
            await Task.Delay(1200, token);
            for (var i = 0; i < flashes; i++)
            {
                var at = clock();
                sent.Add(at);
                await Engine.LiveColorAsync(_targets, "#FFFFFF", 100, "Vertraging meten", token);
                await DelayUntil(clock, at + 600, token);
                await Engine.LiveColorAsync(_targets, "#000000", 100, "Vertraging meten", token);
                progress?.Report(i + 1);
                await DelayUntil(clock, at + 1200, token);
            }
            await Task.Delay(500, token);
            // De eerste 2 flitsen niet meetellen: dan zit je nog in te komen
            List<long> tapCopy;
            lock (taps) tapCopy = taps.ToList();
            return LatencyCalibration.Compute(sent.Skip(2).ToList(), tapCopy.Where(t => t >= sent[Math.Min(2, sent.Count - 1)] - 250).ToList());
        }
        finally
        {
            Calibrating = false;
            if (!queueWasPaused) Queue.Resume();
        }
    }

    private static async Task DelayUntil(Func<long> clock, long target, CancellationToken token)
    {
        var wait = target - clock();
        if (wait > 0) await Task.Delay((int)wait, token);
    }
    public event Action? ModeChanged;

    /// <summary>
    /// Schakelt tussen Stream (BetterTok → lampen) en Spotify (nummer dat speelt → lampen).
    /// De andere bron wordt helemaal uitgezet, zodat ze elkaar nooit storen.
    /// </summary>
    public async Task SetModeAsync(AppMode mode)
    {
        Settings.General.Mode = mode;
        Store.RequestSave();
        if (mode == AppMode.Spotify)
        {
            StopAutomation();
            await BetterTok.DisconnectAsync();
            Spotify.Start();
        }
        else
        {
            await Spotify.StopAsync();
            Queue.StopAll();
            await StartAutomationAsync();
        }
        ModeChanged?.Invoke();
        AutomationStateChanged?.Invoke();
    }

    /// <summary>Bij het opstarten: de laatst gekozen modus starten.</summary>
    public async Task StartLastModeAsync()
    {
        ApplyDonateWave();   // tips komen los van BetterTok binnen (in Spotify-modus worden ze genegeerd)
        Overlays.Apply();
        if (Mode == AppMode.Spotify) { Spotify.Start(); ModeChanged?.Invoke(); return; }
        if (Settings.General.StartAutomationOnLaunch) await StartAutomationAsync();
        else if (Settings.BetterTok.AutoConnect) await ConnectBetterTokAsync();
    }

    public void Initialize()
    {
        Store.Dispatch = a => Run(a);
        Store.Load();
        if (Store.LoadWarning != null) Errors.Report("app.load", Store.LoadWarning);
        Store.Saved += (ok, err) => { if (!ok) Errors.Report("app.save", err); else Errors.Resolve("app.save"); };
        History.Load();
        Govee.CommandDelayMs = Settings.Lighting.EffectiveCommandDelayMs;
        if (!Govee.StartListener()) Errors.Report("gv.port", Govee.ListenerError);

        WatchDevices();
        WatchShowTier();
        Engine.Frame += f => { LastFrame = f; FrameChanged?.Invoke(f); };
        Queue.Finished += OnQueueFinished;
        Queue.NoTargets += () => Errors.Report("gv.nodevice");
        Queue.Start();

        BetterTok.RawMessage += raw => History.AppendRaw(raw);
        BetterTok.EventReceived += e => Run(() => HandleEvent(e));
        DonateWave.EventReceived += e => Run(() => HandleEvent(e));
        DonateWave.CursorChanged += c => Run(() => { Settings.DonateWave.Cursor = c; Store.RequestSave(); });

        _pollTimer = new Timer(_ => _ = PollDevicesAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
    }

    private void Run(Action action)
    {
        if (Dispatch != null) Dispatch(action); else action();
    }

    // ------------------------------------------------------------------ automatisering

    public async Task StartAutomationAsync()
    {
        // Automatisering starten = Stream-modus (BetterTok). Staat Spotify aan, dan eerst terugschakelen.
        if (Mode == AppMode.Spotify) { await SetModeAsync(AppMode.Stream); return; }
        IsRunning = true;
        AutomationStateChanged?.Invoke();
        if (BetterTok.State is ConnectionState.Disconnected or ConnectionState.Failed)
            await ConnectBetterTokAsync();
    }

    public void StopAutomation()
    {
        IsRunning = false;
        Queue.StopAll();
        AutomationStateChanged?.Invoke();
    }

    public void SetSafeMode(bool on)
    {
        Settings.General.SafeMode = on;
        if (on) Queue.StopAll();
        AutomationStateChanged?.Invoke();
    }

    public Task<bool> ConnectBetterTokAsync() =>
        BetterTok.ConnectAsync(Settings.BetterTok, SecretProtector.Unprotect(Settings.BetterTok.ApiKeyProtected));

    public void SetApiKey(string key)
    {
        Settings.BetterTok.ApiKeyProtected = SecretProtector.Protect(key.Trim());
        Store.RequestSave();
    }

    public string GetApiKey() => SecretProtector.Unprotect(Settings.BetterTok.ApiKeyProtected);

    // ------------------------------------------------------------------ DonateWave

    /// <summary>Slaat de DonateWave-sleutel versleuteld op (DPAPI). Een nieuwe sleutel = nieuwe koppeling, dus cursor opnieuw.</summary>
    public void SetDonateWaveKey(string key)
    {
        var k = (key ?? "").Trim();
        if (k == GetDonateWaveKey()) return;
        Settings.DonateWave.ApiKeyProtected = SecretProtector.Protect(k);
        Settings.DonateWave.Cursor = "";
        Store.RequestSave();
    }

    public string GetDonateWaveKey() => SecretProtector.Unprotect(Settings.DonateWave.ApiKeyProtected);

    /// <summary>
    /// Verbindingsdetails voor foutzoeken: adressen, HTTP-statussen, foutcodes en tijden.
    /// Bevat nooit sleutels (alleen gemaskeerd).
    /// </summary>
    public string ConnectionReport()
    {
        var sb = new System.Text.StringBuilder();
        string T(DateTime? t) => t is { } x ? x.ToString("HH:mm:ss") : "–";
        sb.AppendLine($"Stream Light Control {typeof(AutomationController).Assembly.GetName().Version} · {DateTime.Now:dd-MM-yyyy HH:mm:ss} · modus {Mode}");
        sb.AppendLine();
        var dw = DonateWave; var dwo = Settings.DonateWave;
        sb.AppendLine("DONATEWAVE");
        sb.AppendLine($"  Aan:               {(dwo.Enabled ? "ja" : "nee")}");
        sb.AppendLine($"  Status:            {dw.State} – {dw.StatusText}");
        sb.AppendLine($"  API-adres:         GET {dw.ApiUrl}?after={(string.IsNullOrEmpty(dw.Cursor) ? "0" : dw.Cursor)}");
        var key = GetDonateWaveKey();
        sb.AppendLine($"  Sleutel:           {(key.Length == 0 ? "niet ingevuld" : SecretProtector.Mask(key))} (in Authorization: Bearer …)");
        var last = dw.LastRequest;
        sb.AppendLine($"  Laatste HTTP:      {(last == null ? "nog geen verzoek" : last.StatusLabel + $" · {last.Ms} ms · {last.Time:HH:mm:ss}")}");
        if (last != null && last.Info.Length > 0) sb.AppendLine($"  Antwoord:          {last.Info}");
        if (dw.LastFailedRequest is { } lf)
            sb.AppendLine($"  Laatste fout:      {lf.StatusLabel} · {lf.Time:HH:mm:ss} · {lf.Info}");
        sb.AppendLine($"  Verzoeken:         {dw.RequestCount} · volgende controle {T(dw.NextPollAt)}");
        sb.AppendLine($"  Cursor:            {(string.IsNullOrEmpty(dw.Cursor) ? "0 (nog niets verwerkt)" : dw.Cursor)}");
        sb.AppendLine($"  Correctie:         {dwo.OverlayDelayMs} ms (0 = zoals gemeten)");
        sb.AppendLine($"  Gemeten tijden:    gewone tip sound {DwArriveToSoundMs} ms · shout-out animatie +{DwArriveToAnimMs} ms · sound {DwAnimEndToSoundMs} ms na de animatie");
        sb.AppendLine($"                     animatie→animatie {dwo.BetweenAnimationsMs} ms · alert klaar→animatie {DwAlertEndToAnimMs} ms · alert klaar→sound {DwAlertEndToSoundMs} ms");
        sb.AppendLine($"  Overlay-wachtrij:  voorleesstem na de sound {dwo.AlertGapMs} ms · overlay vrij om {(_dwOverlayFreeUtc > DateTime.UtcNow ? _dwOverlayFreeUtc.ToLocalTime().ToString("HH:mm:ss") : "nu")}");
        sb.AppendLine($"  Tips deze sessie:  {dw.TipsReceived}{(dw.LastBatchText.Length > 0 ? " · laatst: " + dw.LastBatchText : "")}");
        var reqs = dw.RecentRequests();
        if (reqs.Count > 0)
        {
            sb.AppendLine("  Laatste verzoeken:");
            foreach (var r in reqs.Take(10))
                sb.AppendLine($"    {r.Time:HH:mm:ss}  {(r.Status?.ToString() ?? "---"),3}  {r.Ms,5} ms  {r.Info}");
        }
        sb.AppendLine();
        var bt = BetterTok;
        sb.AppendLine("BETTERTOK");
        sb.AppendLine($"  Status:            {bt.State} – {bt.StatusText}");
        sb.AppendLine($"  Modus:             {Settings.BetterTok.Mode}{(bt.ConnectedMode is { } cm ? $" (verbonden via {cm})" : "")}");
        sb.AppendLine($"  Adres:             {bt.ConnectedUrl ?? (Settings.BetterTok.Mode == BetterTokMode.Cloud ? BetterTokClient.CloudUrl : Settings.BetterTok.LocalUrl)}");
        sb.AppendLine($"  Laatste bericht:   {T(bt.LastMessageAt)} · herverbindpogingen {bt.ReconnectAttempt}");
        sb.AppendLine();
        sb.AppendLine("GOVEE");
        foreach (var d in Settings.Devices)
            sb.AppendLine($"  {d.Name} {d.Ip} ({d.Sku}) · {(d.Enabled ? "actief" : "uit")} · {(d.Online switch { true => "online", false => "reageert niet", _ => "onbekend" })}");
        if (Settings.Devices.Count == 0) sb.AppendLine("  geen apparaat ingesteld");
        sb.AppendLine($"  Direct springen: {(Settings.Lighting.DirectColor ? "aan" : "uit")} · vertraging {Settings.Lighting.LatencyMs} ms");
        var errs = Errors.Snapshot();
        sb.AppendLine();
        sb.AppendLine($"MELDINGEN ({errs.Count})");
        foreach (var e in errs) sb.AppendLine($"  [{e.Key}] {e.Title}{(string.IsNullOrEmpty(e.Detail) ? "" : " – " + e.Detail)}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ DonateWave-overlaywachtrij

    private DateTime _dwOverlayFreeUtc = DateTime.MinValue;
    private readonly object _dwGate = new();
    private readonly List<DwAlert> _dwAlerts = new();
    private DateTime _dwBaseFreeUtc = DateTime.MinValue;   // overlay vrij na alerts die al uit de lijst zijn

    /// <summary>Eén alert in de DonateWave-overlay (gewone tip of shout-out).</summary>
    private sealed class DwAlert
    {
        public DateTime Arrive;          // moment van de tip (created_at) + eigen correctie
        public int AnimMs;               // 0 = gewone tip zonder animatie
        public int SoundMs;              // sound × quantity
        public QueueItem? Item;
        public DateTime SoundStart, End;
    }

    // Gemeten op de echte DonateWave-overlay (12-11-2026, 7 tests, tijden t.o.v. created_at uit de API):
    public const int DwArriveToAnimMs = 790;        // shout-out: animatie in beeld
    public const int DwArriveToSoundMs = -140;      // gewone tip: sound begint (iets vóór created_at)
    public const int DwAnimEndToSoundMs = 795;      // einde animatievideo → extra sound hoorbaar
    public const int DwAlertEndToAnimMs = 2437;     // alert klaar → volgende animatie in beeld
    public const int DwAlertEndToSoundMs = 1500;    // alert klaar → volgende (gewone of uitgestelde) sound
    public const int DwMinAlertMs = 2200;           // overlay-code: alert minimaal 2,2 s vanaf de sound

    /// <summary>
    /// Het moment (UTC) waarop de sound van deze tip in de overlay begint. De app speelt de wachtrij van de overlay na:
    ///   · gewone tip: sound direct; shout-out: animatie, dan de sound;
    ///   · komt er een alert binnen terwijl een animatie loopt, dan schuift de sound van díe shout-out naar achteren:
    ///     animatie 1 → animatie 2 + sound 2 → … → sound 1 (zo doet DonateWave het, gemeten met 2 en 3 tegelijk);
    ///   · na elke sound de voorleesstem (AlertGapMs), pas dan de volgende.
    /// Al ingeplande shows schuiven mee (de wachtrij leest het startmoment steeds opnieuw).
    /// </summary>
    public DateTime DonateWaveSoundStart(DateTime occurredUtc, int animationMs, int soundMs, int quantity = 1) =>
        DonateWaveSchedule(occurredUtc, animationMs, soundMs, quantity).Start;

    private (DateTime Start, Action<QueueItem> Attach) DonateWaveSchedule(DateTime occurredUtc, int animationMs, int soundMs, int quantity = 1)
    {
        lock (_dwGate)
        {
            var now = DateTime.UtcNow;
            // Alerts die allang klaar zijn uit de simulatie halen (alleen vooraan, zodat de volgorde klopt)
            while (_dwAlerts.Count > 0 && _dwAlerts[0].End < now.AddSeconds(-10) && _dwAlerts.All(x => x.End < now.AddSeconds(-10) || x.Arrive > _dwAlerts[0].End))
            {
                if (_dwAlerts[0].End > _dwBaseFreeUtc) _dwBaseFreeUtc = _dwAlerts[0].End;
                _dwAlerts.RemoveAt(0);
            }
            var me = new DwAlert
            {
                Arrive = occurredUtc.AddMilliseconds(Settings.DonateWave.OverlayDelayMs),
                AnimMs = Math.Max(0, animationMs),
                SoundMs = Math.Max(0, soundMs) * Math.Clamp(quantity, 1, 50)
            };
            // Op volgorde van binnenkomst (stabiel: bij gelijke tijd de eerst ontvangen eerst)
            var pos = _dwAlerts.Count;
            while (pos > 0 && _dwAlerts[pos - 1].Arrive > me.Arrive) pos--;
            _dwAlerts.Insert(pos, me);
            SimulateOverlay();
            foreach (var x in _dwAlerts)
                if (x.Item is { } it && x != me) Retime(it, x.SoundStart, now);
            return (me.SoundStart, qi => { lock (_dwGate) { me.Item = qi; Retime(qi, me.SoundStart, DateTime.UtcNow); } });
        }
    }

    /// <summary>Voor tests en de foutenpagina: geplande sound-starts in volgorde van binnenkomst.</summary>
    public IReadOnlyList<DateTime> DonateWavePlannedStarts() { lock (_dwGate) return _dwAlerts.Select(x => x.SoundStart).ToList(); }

    private static void Retime(QueueItem it, DateTime start, DateTime now)
    {
        it.StartAtUtc = start;
        it.MaxWaitMs = (int)Math.Max(0, (start - now).TotalMilliseconds) + 1500 + (it.WaitStartedUtc is { } ws ? (int)(now - ws).TotalMilliseconds : 0);
    }

    /// <summary>Speelt de overlay-wachtrij na en vult SoundStart/End van elke alert in.</summary>
    private void SimulateOverlay()
    {
        var tts = Settings.DonateWave.AlertGapMs;
        var betweenAnims = Settings.DonateWave.BetweenAnimationsMs;
        var queue = new LinkedList<(DwAlert A, bool Sound)>();
        var next = 0;
        var t = _dwBaseFreeUtc;              // einde van de vorige fase
        var lastWasAnim = false;             // vorige fase was een animatie (sound uitgesteld)
        void Arrivals(DateTime upTo)
        {
            while (next < _dwAlerts.Count && _dwAlerts[next].Arrive <= upTo)
            {
                var a = _dwAlerts[next++];
                if (a.AnimMs == 0) { queue.AddLast((a, true)); continue; }
                // Shout-outs hebben voorrang: vóór wachtende gewone tips (niet vóór andere shout-outs)
                var node = queue.First;
                while (node != null && !(node.Value.Sound && node.Value.A.AnimMs == 0)) node = node.Next;
                if (node == null) queue.AddLast((a, false)); else queue.AddBefore(node, (a, false));
            }
        }
        while (next < _dwAlerts.Count || queue.Count > 0)
        {
            Arrivals(t);
            if (queue.Count == 0) { Arrivals(_dwAlerts[next].Arrive); }
            var (a, soundPhase) = queue.First!.Value; queue.RemoveFirst();
            DateTime soundStart;
            if (!soundPhase)
            {
                var visible = a.Arrive.AddMilliseconds(DwArriveToAnimMs);
                var afterPrev = t.AddMilliseconds(lastWasAnim ? betweenAnims : DwAlertEndToAnimMs);
                if (t != DateTime.MinValue && afterPrev > visible) visible = afterPrev;
                var pick = visible.AddMilliseconds(-DwArriveToAnimMs);
                var animEnd = visible.AddMilliseconds(a.AnimMs);
                Arrivals(animEnd);
                // Kwam er een andere shout-out binnen terwijl deze animatie liep (of tegelijk), dan gaat die eerst
                var arrivedDuring = false;
                foreach (var (q, ph) in queue) if (!ph && q != a && q.Arrive >= pick && q.Arrive <= animEnd) arrivedDuring = true;
                if (arrivedDuring)
                {
                    queue.AddLast((a, true));     // sound van deze shout-out schuift achteraan
                    t = animEnd; lastWasAnim = true;
                    continue;
                }
                soundStart = animEnd.AddMilliseconds(DwAnimEndToSoundMs);
            }
            else
            {
                var own = a.AnimMs == 0 ? a.Arrive.AddMilliseconds(DwArriveToSoundMs) : DateTime.MinValue;
                var afterPrev = t == DateTime.MinValue ? DateTime.MinValue : t.AddMilliseconds(DwAlertEndToSoundMs);
                soundStart = own > afterPrev ? own : afterPrev;
            }
            a.SoundStart = soundStart;
            var end = soundStart.AddMilliseconds(Math.Max(a.SoundMs + tts, DwMinAlertMs));
            a.End = end;
            t = end; lastWasAnim = false;
        }
        _dwOverlayFreeUtc = t;
    }

    /// <summary>Start of stopt het ophalen van DonateWave-tips volgens de instellingen.</summary>
    public void ApplyDonateWave()
    {
        if (Settings.DonateWave.Enabled) DonateWave.Start(GetDonateWaveKey(), Settings.DonateWave.Cursor);
        else DonateWave.Stop();
    }

    // ------------------------------------------------------------------ events

    public HistoryEntry? HandleEvent(StreamEvent e)
    {
        if (Rules.CheckDuplicate(e)) return null;

        // Een lopende combo telt pas mee als hij klaar is (anders dubbel tellen)
        if (e.Type == EventTypes.Gift && e.StreakInProgress && Settings.Gift.StreakMode == GiftStreakMode.AtEnd)
            return null;

        if (!e.IsTest) CountStats(e);

        var entry = new HistoryEntry
        {
            Time = e.ReceivedAt, User = e.UserLabel, Type = e.Type, RawType = e.RawType, GiftName = e.GiftName,
            GiftCount = e.GiftCount, Coins = e.Type == EventTypes.Gift ? e.TotalCoins : null, IsTest = e.IsTest,
            Status = ProcessingStatus.Received
        };

        if (!e.IsTest && SafeMode)
        {
            entry.Status = ProcessingStatus.Ignored;
            entry.Detail = "Safe Mode staat aan";
        }
        else if (!e.IsTest && Mode == AppMode.Spotify)
        {
            entry.Status = ProcessingStatus.Ignored;
            entry.Detail = "Spotify-modus staat aan";
        }
        else if (!e.IsTest && !IsRunning)
        {
            entry.Status = ProcessingStatus.Ignored;
            entry.Detail = "Automatisering staat uit";
        }
        else
        {
            var d = Rules.Evaluate(e, Settings, Queue.CountForTrigger, checkDuplicate: false);
            entry.ShowName = d.Show?.Name ?? "";
            entry.Detail = d.Reason;
            switch (d.Kind)
            {
                case DecisionKind.Play:
                    History.Add(entry);
                    // Moment waarop de sound begint: bron-tijd (DonateWave) of nu, plus de ingestelde wachttijd
                    DateTime? startAt = null;
                    Action<QueueItem>? attach = null;
                    if (e.Type == EventTypes.Tip && e.OccurredAtUtc is { } occ)
                    {
                        var sch = DonateWaveSchedule(occ, d.Trigger!.StartDelayMs, d.Show!.TotalDurationMs, e.GiftCount);
                        startAt = sch.Start; attach = sch.Attach;
                    }
                    else if (d.Trigger!.StartDelayMs > 0) startAt = DateTime.UtcNow.AddMilliseconds(d.Trigger.StartDelayMs);
                    var maxWait = startAt is { } sa ? (int)Math.Max(0, (sa - DateTime.UtcNow).TotalMilliseconds) + 1500 : 0;
                    // DonateWave speelt de sound 'quantity' keer achter elkaar → show net zo vaak
                    var playShow = d.Show!;
                    if (e.Type == EventTypes.Tip && e.GiftCount > 1)
                    {
                        playShow = d.Show!.Clone();
                        playShow.Repeats = Math.Clamp(d.Show!.Repeats * e.GiftCount, 1, 50);
                    }
                    var qi = Queue.Enqueue(playShow, d.Trigger!.Priority, e.UserLabel, e.Type, d.Trigger.Id,
                        e.IsTest ? QueueSource.Test : QueueSource.Event, d.Trigger.InterruptCurrent, entry,
                        startAt, maxWait, d.Trigger.SoundDurationMs);
                    attach?.Invoke(qi);
                    if (e.Type == EventTypes.Tip) Donations.Add(e, TipAlertMoment(e, startAt));
                    EventHandled?.Invoke(entry);
                    return entry;
                case DecisionKind.NoRule:
                    entry.Status = ProcessingStatus.NoRule;
                    break;
                case DecisionKind.ShowMissing:
                    entry.Status = ProcessingStatus.Failed;
                    Errors.Report("ev.noshow", d.Trigger?.Name);
                    break;
                default:
                    entry.Status = ProcessingStatus.Ignored;
                    break;
            }
        }
        if (e.Type == EventTypes.Tip) Donations.Add(e, TipAlertMoment(e, null));
        // Losse likes zonder koppeling niet allemaal in de geschiedenis (kan honderden per minuut zijn)
        if (!(e.Type == EventTypes.Like && (entry.Status is ProcessingStatus.Ignored or ProcessingStatus.NoRule) && !e.IsTest))
            History.Add(entry);
        EventHandled?.Invoke(entry);
        return entry;
    }

    private void CountStats(StreamEvent e)
    {
        Stats.Events++;
        switch (e.Type)
        {
            case EventTypes.Follow: Stats.Follows++; break;
            case EventTypes.Gift: Stats.Gifts += Math.Max(1, e.GiftCount); Stats.Coins += Math.Max(0, e.TotalCoins); break;
            case EventTypes.Like: Stats.Likes += Math.Max(1, e.LikeCount); break;
            case EventTypes.Share: Stats.Shares++; break;
            case EventTypes.Comment: Stats.Comments++; break;
            case EventTypes.Member: Stats.Members++; break;
        }
    }

    private void OnQueueFinished(QueueItem item, FinishReason reason, string? error)
    {
        if (reason == FinishReason.Completed && item.Source == QueueSource.Event)
            Run(() => Stats.ShowsPlayed++);
        if (reason == FinishReason.Dropped) Errors.Report("q.full", $"‘{item.ShowName}’ ({item.EventLabel}) overgeslagen");
        if (reason == FinishReason.Failed && error != null && error != "Geen actief Govee-apparaat")
            Errors.Report("q.play", $"‘{item.ShowName}’: {error}");
        if (item.History == null) return;
        item.History.Status = reason switch
        {
            FinishReason.Completed => ProcessingStatus.Played,
            FinishReason.Interrupted => ProcessingStatus.Interrupted,
            FinishReason.Failed => ProcessingStatus.Failed,
            _ => ProcessingStatus.Skipped
        };
        item.History.Detail = reason switch
        {
            FinishReason.Completed => item.History.Detail,
            FinishReason.Skipped => "Overgeslagen",
            FinishReason.Stopped => "Gestopt",
            FinishReason.Removed => "Verwijderd uit wachtrij",
            FinishReason.Cleared => "Wachtrij leeggemaakt",
            FinishReason.Dropped => "Wachtrij was vol",
            FinishReason.Interrupted => "Onderbroken door belangrijker effect",
            _ => error ?? "Fout"
        };
    }

    // ------------------------------------------------------------------ testen & handmatig

    /// <summary>Speelt de show van één koppeling direct af (testknop), ongeacht cooldowns.</summary>
    public QueueItem? TestTrigger(EventTrigger trigger)
    {
        var show = Settings.FindShow(trigger.ShowId);
        if (show == null) { Errors.Report("ev.noshow", trigger.Name); return null; }
        var entry = new HistoryEntry
        {
            User = "Test", Type = trigger.EventType, RawType = trigger.EventType, IsTest = true,
            GiftName = trigger.GiftNameList.FirstOrDefault() ?? "", Coins = trigger.EventType == EventTypes.Gift ? trigger.MinCoins ?? trigger.MaxCoins : null,
            ShowName = show.Name, Detail = $"Test van ‘{trigger.Name}’"
        };
        History.Add(entry);
        return Queue.Enqueue(show, Math.Max(trigger.Priority, Priorities.Critical), "Test", trigger.EventType, trigger.Id, QueueSource.Test, true, entry);
    }

    /// <summary>Simuleert een event via de volledige verwerking (regels, wachtrij, lampen).</summary>
    public HistoryEntry? Simulate(StreamEvent e) => HandleEvent(e);

    public QueueItem PlayShowNow(LightShow show) =>
        Queue.Enqueue(show, Priorities.Manual, "Jij", "", "", QueueSource.Manual, true);

    public IReadOnlyList<string> Targets => _targets;

    public async Task QuickColorAsync(string hex, int brightness = 100)
    {
        Queue.SkipCurrent();
        await RunDeviceAction(() => Engine.SetStaticAsync(_targets, hex, brightness));
    }

    public Task QuickPowerAsync(bool on)
    {
        if (!on) Queue.StopAll();
        return RunDeviceAction(() => Engine.PowerAllAsync(_targets, on));
    }

    public Task QuickBrightnessAsync(int brightness) => RunDeviceAction(() => Engine.BrightnessAllAsync(_targets, brightness));

    public void StopAllEffects() => Queue.StopAll();

    private async Task RunDeviceAction(Func<Task> action)
    {
        if (_targets.Length == 0) { Errors.Report("gv.nodevice"); throw new InvalidOperationException("Er is nog geen actief Govee-apparaat. Voeg je lamp toe in Device Manager."); }
        try { await action(); Errors.Resolve("gv.send"); }
        catch (Exception ex) when (ex is not InvalidOperationException) { Errors.Report("gv.send", ex.Message); throw; }
    }

    // ------------------------------------------------------------------ club- of veilige versie van de shows

    private bool? _fastTier;

    /// <summary>Kiest per show de club-versie (strip springt direct, snelheidslimiet ≤ 400 ms) of de veilige versie.</summary>
    public int ApplyShowTier(bool force = false)
    {
        var fast = ShowTiers.IsFast(Settings.Lighting);
        if (!force && _fastTier == fast) return 0;
        _fastTier = fast;
        var n = ShowTiers.Apply(Settings);
        if (n > 0) Store.RequestSave();
        return n;
    }

    private void WatchShowTier()
    {
        ApplyShowTier(force: true);
        Settings.Lighting.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LightingOptions.MinChangeMs)) Run(() => ApplyShowTier());
        };
    }

    // ------------------------------------------------------------------ apparaten

    private void WatchDevices()
    {
        void Refresh() => _targets = Settings.Devices.Where(d => d.Enabled && System.Net.IPAddress.TryParse(d.Ip, out _))
            .Select(d => d.Ip).Distinct().ToArray();
        void OnProp(object? s, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(GoveeDeviceConfig.Enabled) or nameof(GoveeDeviceConfig.Ip)) Refresh(); }
        foreach (var d in Settings.Devices) d.PropertyChanged += OnProp;
        Settings.Devices.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (GoveeDeviceConfig d in e.NewItems) d.PropertyChanged += OnProp;
            if (e.OldItems != null) foreach (GoveeDeviceConfig d in e.OldItems) d.PropertyChanged -= OnProp;
            if (e.Action == NotifyCollectionChangedAction.Reset) foreach (var d in Settings.Devices) { d.PropertyChanged -= OnProp; d.PropertyChanged += OnProp; }
            Refresh();
        };
        Refresh();
    }

    public async Task<DeviceStatus?> TestDeviceAsync(GoveeDeviceConfig device)
    {
        var status = await Govee.GetStatusAsync(device.Ip, 2000);
        Run(() => ApplyStatus(device, status));
        return status;
    }

    private void ApplyStatus(GoveeDeviceConfig device, DeviceStatus? status)
    {
        var wasOnline = device.Online;
        device.Online = status != null;
        if (status != null) { device.Status = status; device.LastSeen = DateTime.Now; }
        if (status == null && wasOnline == true) Errors.Report("gv.noresponse", $"{device.Name} ({device.Ip})");
        if (status != null && Settings.Devices.All(d => d.Online != false)) Errors.Resolve("gv.noresponse");
        DevicesStatusChanged?.Invoke();
    }

    private async Task PollDevicesAsync()
    {
        if (!Settings.Lighting.StatusPolling || Queue.Current != null || Spotify.Running) return;
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var devices = Settings.Devices.ToList();
            foreach (var d in devices)
            {
                if (!System.Net.IPAddress.TryParse(d.Ip, out _)) continue;
                var status = await Govee.GetStatusAsync(d.Ip, 1500);
                Run(() => ApplyStatus(d, status));
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    /// <summary>Zoekt opnieuw op het netwerk en werkt het IP bij als de lamp een nieuw adres kreeg (DHCP).</summary>
    public async Task<string> ReconnectDeviceAsync(GoveeDeviceConfig device)
    {
        var found = await Govee.DiscoverAsync(2500);
        var match = found.FirstOrDefault(f => !string.IsNullOrEmpty(device.Mac) && f.Mac.Equals(device.Mac, StringComparison.OrdinalIgnoreCase))
                    ?? found.FirstOrDefault(f => f.Ip == device.Ip)
                    ?? (found.Count == 1 && string.IsNullOrEmpty(device.Mac) ? found[0] : null);
        var message = "";
        if (match != null)
        {
            Run(() =>
            {
                if (match.Ip != device.Ip) { message = $"Nieuw IP-adres gevonden: {match.Ip} (was {device.Ip}). "; device.Ip = match.Ip; }
                if (string.IsNullOrEmpty(device.Mac)) device.Mac = match.Mac;
                if (string.IsNullOrEmpty(device.Sku)) device.Sku = match.Sku;
            });
        }
        var status = await TestDeviceAsync(device);
        return status != null
            ? message + "Verbonden – LAN Control is actief."
            : message + (match == null ? "Niet gevonden op het netwerk. " : "") + "Geen antwoord van de lamp.";
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Dispose();
        Spotify.Dispose();
        Queue.Dispose();
        BetterTok.Dispose();
        DonateWave.Dispose();
        Overlays.Dispose();
        Store.SaveNow();
        Store.Dispose();
        History.Dispose();
        Govee.Dispose();
    }
}
