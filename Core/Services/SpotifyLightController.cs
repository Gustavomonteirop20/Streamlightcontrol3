using System.Diagnostics;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// Spotify-modus: kijkt welk nummer er speelt, meet het geluid en stuurt de lampen.
/// Elk nummer krijgt zijn eigen kleuren. Nooit sneller dan de snelheidslimiet van de strip.
/// </summary>
public sealed class SpotifyLightController : IDisposable
{
    private readonly LightShowEngine _engine;
    private readonly Func<IReadOnlyList<string>> _targets;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _busy;
    private readonly ErrorCenter? _errors;
    private readonly INowPlayingSource _source;
    private readonly Func<IAudioLevelSource?> _audioFactory;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SpotifyLightController(LightShowEngine engine, Func<IReadOnlyList<string>> targets, Func<AppSettings> settings,
        Func<bool>? busy = null, ErrorCenter? errors = null, INowPlayingSource? source = null, Func<IAudioLevelSource?>? audioFactory = null)
    {
        _engine = engine;
        _targets = targets;
        _settings = settings;
        _busy = busy ?? (() => false);
        _errors = errors;
        _source = source ?? new SpotifyWindowSource();
        _audioFactory = audioFactory ?? (() => new WindowsAudioMeter());
    }

    // ------------------------------------------------------------ live toestand (voor de interface)

    public bool Running { get; private set; }
    public PlayerSnapshot Snapshot { get; private set; } = PlayerSnapshot.NotRunning;
    public string[] Palette { get; private set; } = Array.Empty<string>();
    public string CurrentColor { get; private set; } = "#000000";
    public double Level { get; private set; }
    public bool AudioAvailable { get; private set; }
    public long BeatCount { get; private set; }
    public int ChangeCount { get; private set; }
    public DateTime? LastBeatAt { get; private set; }
    /// <summary>Herkend tempo (0 = nog niet vast).</summary>
    public double Bpm { get; private set; }
    public int FlashCount { get; private set; }
    /// <summary>Rave/zaag herkend: festival-licht zoals bij de dono-sounds.</summary>
    public bool RaveActive { get; private set; }
    /// <summary>In een drop (vaste kick + energie) tijdens rave.</summary>
    public bool RaveInDrop { get; private set; }
    public int BlackCount { get; private set; }

    /// <summary>Ander nummer, pauze/afspelen, gestart of gestopt.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (Running) return;
        Running = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token));
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (!Running) return;
        Running = false;
        _cts?.Cancel();
        try { if (_loop != null) await _loop; } catch { }
        _cts?.Dispose(); _cts = null; _loop = null;
        Snapshot = PlayerSnapshot.NotRunning;
        Level = 0;
        _errors?.Resolve("sp.notrunning");
        _errors?.Resolve("sp.audio");
        Changed?.Invoke();
    }

    // ------------------------------------------------------------ de lus

    private async Task LoopAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var detector = new BeatDetector();
        IAudioLevelSource? audio = null;
        try { audio = _audioFactory(); } catch { }
        long nextPoll = 0, lastSend = long.MinValue / 2, audioFailSince = -1, notRunningSince = -1;
        string? trackKey = null;
        var force = true;           // eerste keer altijd een kleur sturen
        var pausedSent = false;
        var wasBusy = false;
        var scene = 0; var stepInScene = 0;
        var pulseHigh = true;
        var tempo = new TempoTracker();
        var specials = new Queue<string>();     // flashbang-reeks bij een drop: wit, zwart, wit
        var change = 0;                         // teller voor het patroon (zwart stukje / flashbang)
        long nextTarget = -1;                   // voorspelde beat waarop de volgende wissel moet vallen
        var rave = new RaveDetector();
        var plan = new Queue<(long at, string hex, string? hexB, int bri, string label)>();
        long lastPlanned = long.MinValue / 2; int dropBeat = 0, breakBeat = 0, raveColor = 0;
        _engine.ForgetState();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = clock.ElapsedMilliseconds;
                var s = _settings();
                var opt = s.Spotify;

                // --- welk nummer speelt er? (± elke 0,7 s)
                if (now >= nextPoll)
                {
                    nextPoll = now + 700;
                    PlayerSnapshot snap;
                    try { snap = _source.Read(); } catch { snap = PlayerSnapshot.NotRunning; }
                    var key = snap.Track?.Key;
                    var stateChanged = snap.State != Snapshot.State || key != Snapshot.Track?.Key;
                    Snapshot = snap;
                    if (key != null && key != trackKey)
                    {
                        trackKey = key;
                        Palette = SpotifyPalette.For(key);
                        scene = 0; stepInScene = 0; pulseHigh = true; change = 0;
                        specials.Clear(); nextTarget = -1;
                        rave.Reset(); plan.Clear(); RaveInDrop = false; dropBeat = 0;
                        force = true;
                    }
                    if (snap.State == PlayerState.NotRunning)
                    {
                        if (notRunningSince < 0) notRunningSince = now;
                        else if (now - notRunningSince > 5000) _errors?.Report("sp.notrunning");
                    }
                    else { notRunningSince = -1; _errors?.Resolve("sp.notrunning"); }
                    if (stateChanged) Changed?.Invoke();
                }

                // --- geluid meten
                float? peak = null;
                try { peak = audio?.ReadPeak(); } catch { }
                if (peak == null)
                {
                    if (AudioAvailable || audioFailSince < 0) audioFailSince = audioFailSince < 0 ? now : audioFailSince;
                    AudioAvailable = false;
                    if (now - audioFailSince > 4000) _errors?.Report("sp.audio", (audio as WindowsAudioMeter)?.LastError);
                }
                else
                {
                    if (!AudioAvailable) _errors?.Resolve("sp.audio");
                    AudioAvailable = true; audioFailSince = -1;
                }
                var minGap = Math.Max(s.Lighting.MinChangeMs, 300);
                detector.Sensitivity = opt.Sensitivity / 100.0;
                detector.MinIntervalMs = 250;
                var beat = peak != null && detector.Push(peak.Value, now);
                Level = peak == null ? 0 : Math.Clamp(detector.Level, 0, 1);
                if (beat) { BeatCount++; LastBeatAt = DateTime.Now; tempo.AddBeat(now); }
                tempo.Update(now);
                Bpm = tempo.Bpm;
                rave.Update(now, beat, tempo, detector);
                var raveOn = AudioAvailable && tempo.Locked && Snapshot.State == PlayerState.Playing &&
                             (opt.Rave == RaveMode.Always || (opt.Rave == RaveMode.Auto && rave.IsRave));
                if (raveOn != RaveActive) { RaveActive = raveOn; plan.Clear(); Changed?.Invoke(); }
                var dropHit = peak != null && detector.CheckDrop(now);
                if (dropHit && opt.Flashbangs && specials.Count == 0)
                {
                    specials.Enqueue("#FFFFFF"); specials.Enqueue("#000000"); specials.Enqueue("#FFFFFF");
                }

                // --- lampen sturen
                var targets = _targets();
                var busy = _busy();
                if (busy) { wasBusy = true; }
                else if (wasBusy) { wasBusy = false; force = true; pausedSent = false; _engine.ForgetState(); }

                var canSend = !busy && !s.General.SafeMode && targets.Count > 0;
                if (canSend)
                {
                    var since = now - lastSend;
                    var maxBri = opt.MaxBrightness;
                    // Directe modus valt na ±1 minuut zonder data terug: bij stilstand (pauze, Rustig) de kleur af en toe herhalen
                    if (s.Lighting.DirectColor && since >= 25_000 && _lastHex != null)
                        if (await Send(targets, _lastHex, _lastBri, _lastLabel, token)) { lastSend = now; since = 0; }
                    if (Snapshot.State != PlayerState.Playing)
                    {
                        if (!pausedSent && opt.DimWhenPaused && since >= minGap)
                        {
                            var hex = Palette.Length > 0 ? Palette[0] : "#8A00FF";
                            if (await Send(targets, hex, Math.Max(5, maxBri / 5), "Spotify – gepauzeerd", token)) { lastSend = now; pausedSent = true; }
                        }
                        else if (!opt.DimWhenPaused) pausedSent = true;
                    }
                    else if (Palette.Length == SpotifyPalette.Colors.Length)
                    {
                        if (pausedSent) { pausedSent = false; force = true; }
                        var label = "♫ " + (Snapshot.Track?.Label ?? "Spotify");
                        var quiet = AudioAvailable && detector.IsSilent(now);
                        // dynamiek: zachte stukken minder fel, harde stukken vol
                        var bri = (int)Math.Round(maxBri * (AudioAvailable ? 0.55 + 0.45 * Math.Clamp(Level * 1.4, 0, 1) : 1.0));
                        if (RaveActive)
                        {
                            // ---------------- RAVE / ZAAG: festival-licht op de voorspelde beats ----------------
                            var I = tempo.IntervalMs; var lat = s.Lighting.LatencyMs;
                            var fast = s.Lighting.MinChangeMs <= 120;                 // strip springt direct: ¼-beat-flashes kunnen
                            var every = fast ? 1 : Math.Max(1, (int)Math.Ceiling(s.Lighting.MinChangeMs / I));
                            if (plan.Count == 0)
                            {
                                var T = tempo.NextBeatAtOrAfter(Math.Max(now + lat + 5, lastPlanned + (long)(I * 0.5)));
                                if (T > 0)
                                {
                                    lastPlanned = T;
                                    var kicking = rave.RecentKicks(now, I, 4) >= 3 && detector.Energetic;
                                    var entering = (kicking || dropHit) && !RaveInDrop;
                                    if (entering) { RaveInDrop = true; dropBeat = 0; raveColor += 2; }
                                    else if (!kicking && RaveInDrop && rave.RecentKicks(now, I, 4) <= 1) { RaveInDrop = false; breakBeat = 0; }
                                    string Col(int k) => Palette[((raveColor + k) % Palette.Length + Palette.Length) % Palette.Length];
                                    long q = (long)(I / 4);
                                    if (!RaveInDrop)
                                    {
                                        // break in een raveplaat: rustiger, per beat pulseren in één kleur
                                        if (breakBeat % every == 0) plan.Enqueue((T, Col(0), null, breakBeat % 2 == 0 ? (int)(maxBri * 0.9) : (int)(maxBri * 0.65), "rave · break"));
                                        breakBeat++;
                                    }
                                    else
                                    {
                                        var bar = dropBeat / 4;
                                        var x = Col(dropBeat % 2); var y = Col(dropBeat % 2 + 1);
                                        if (!fast)
                                        {
                                            if (dropBeat % every == 0) plan.Enqueue((T, dropBeat % 4 == 0 ? "#FFFFFF" : x, null, maxBri, "rave · drop"));
                                        }
                                        else if (dropBeat == 0 || bar % 4 == 3)
                                        {
                                            // strobe: wit ↔ kleur op ¼ beats (de klap van de drop, en elke 4e maat)
                                            for (var k = 0; k < 4; k++) plan.Enqueue((T + k * q, k % 2 == 0 ? "#FFFFFF" : (k == 1 ? x : y), null, maxBri, "rave · strobe"));
                                        }
                                        else if (dropBeat >= 32)
                                        {
                                            // na 8 maten: intenser — ½-beat-wissel, strip A/B om-en-om
                                            plan.Enqueue((T, "#FFFFFF", null, maxBri, "rave · flash op de kick"));
                                            plan.Enqueue((T + q, x, y, maxBri, "rave · strip A/B"));
                                            plan.Enqueue((T + 2 * q, y, x, maxBri, "rave · strip A/B"));
                                        }
                                        else
                                        {
                                            plan.Enqueue((T, "#FFFFFF", null, maxBri, "rave · flash op de kick"));
                                            plan.Enqueue((T + q, x, null, maxBri, "rave · kleur op de beat"));
                                        }
                                        dropBeat++;
                                        if (dropBeat % 8 == 0) raveColor += 2;      // elke 2 maten een nieuw kleurenpaar
                                    }
                                }
                            }
                            while (plan.Count > 0 && plan.Peek().at - lat <= clock.ElapsedMilliseconds)
                            {
                                var ev = plan.Dequeue();
                                if (clock.ElapsedMilliseconds - (ev.at - lat) > I) continue;          // te laat: overslaan
                                if (await Send(targets, ev.hex, ev.bri, "♫ " + ev.label, token, ev.hexB))
                                {
                                    lastSend = clock.ElapsedMilliseconds; force = false;
                                    if (ev.hex == "#FFFFFF") FlashCount++;
                                }
                            }
                        }
                        else switch (opt.Style)
                        {
                            case SpotifyStyle.Beat:
                            {
                                var lat = s.Lighting.LatencyMs;
                                var gap = quiet ? minGap * 2 : minGap;
                                bool go;
                                if (tempo.Locked && !force)
                                {
                                    // Tempo bekend: de wissel valt op de VOORSPELDE beat, verzonden 'vertraging' ms eerder.
                                    // Zo komt het licht tegelijk met de klap in plaats van erna.
                                    if (nextTarget < 0 || nextTarget < now - tempo.IntervalMs)
                                        nextTarget = tempo.NextBeatAtOrAfter(Math.Max(now + lat, lastSend + lat + (long)(gap * 0.95)));
                                    go = now >= nextTarget - lat;
                                }
                                else
                                {
                                    // Tempo nog niet bekend: reageren op de klap; geen beat te horen → rustig vast tempo
                                    var idle = AudioAvailable ? Math.Max(minGap * 4, 2400) : Math.Max(minGap * 2, 1000);
                                    go = (force || beat || (since >= idle && !quiet)) && since >= gap;
                                    nextTarget = -1;
                                }
                                if (go && (force || since >= gap * 0.95))
                                {
                                    // Wat sturen we? Drop-reeks > nieuwe kleur van het nummer > patroon met zwart en flashbang.
                                    string hex; int b2;
                                    var energetic = !AudioAvailable || detector.Energetic;
                                    var slot = change % 8;
                                    if (specials.Count > 0) { hex = specials.Dequeue(); b2 = maxBri; }
                                    else if (!force && energetic && opt.Flashbangs && slot == 7) { hex = "#FFFFFF"; b2 = maxBri; }
                                    else if (!force && energetic && opt.Blackouts && slot == 3) { hex = "#000000"; b2 = maxBri; }
                                    else
                                    {
                                        // Scènes: 4 wissels tussen een kleurenpaar, dan het volgende paar (alle 8 kleuren komen langs)
                                        hex = Palette[(scene * 2 + stepInScene % 2) % Palette.Length];
                                        b2 = force || energetic ? maxBri : bri;
                                        if (++stepInScene >= 4) { stepInScene = 0; scene = (scene + 1) % (Palette.Length / 2); }
                                    }
                                    if (await Send(targets, hex, b2, label, token))
                                    {
                                        if (hex == "#FFFFFF") FlashCount++;
                                        if (hex == "#000000") BlackCount++;
                                        lastSend = clock.ElapsedMilliseconds; force = false; change++;
                                        // volgende wissel: eerste voorspelde beat die minstens de snelheidslimiet verder ligt
                                        nextTarget = tempo.Locked ? tempo.NextBeatAtOrAfter(lastSend + lat + (long)(gap * 0.95)) : -1;
                                    }
                                }
                                break;
                            }
                            case SpotifyStyle.Calm:
                            {
                                var hold = opt.CalmSeconds * 1000L;
                                if ((force || since >= hold) && since >= minGap)
                                {
                                    var to = Palette[stepInScene % Palette.Length];
                                    var ok = force
                                        ? await Send(targets, to, bri, label, token)
                                        : await FadeAsync(targets, CurrentColor, to, bri, minGap, label, token);
                                    if (ok) { lastSend = clock.ElapsedMilliseconds; force = false; stepInScene++; }
                                }
                                break;
                            }
                            case SpotifyStyle.PerSong:
                            {
                                // Eén kleur per nummer; op de beat pulseert de helderheid (vol ↔ half); drop = flashbangs
                                if ((force || beat || specials.Count > 0) && since >= minGap)
                                {
                                    var hex = Palette[0]; var b2 = maxBri;
                                    if (specials.Count > 0 && !force) hex = specials.Dequeue();
                                    else { pulseHigh = force || !pulseHigh; b2 = pulseHigh ? maxBri : Math.Max(5, maxBri / 2); }
                                    if (await Send(targets, hex, b2, label, token))
                                    {
                                        if (hex == "#FFFFFF") FlashCount++;
                                        lastSend = now; force = false;
                                    }
                                }
                                break;
                            }
                        }
                    }
                }

                await Task.Delay(20, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { audio?.Dispose(); } catch { }
        }
    }

    private string? _lastHex;
    private int _lastBri = 100;
    private string _lastLabel = "Spotify";

    private async Task<bool> Send(IReadOnlyList<string> targets, string hex, int bri, string label, CancellationToken token, string? hexB = null)
    {
        try
        {
            await _engine.LiveColorAsync(targets, hex, hexB, bri, label, token);
            _lastHex = hex; _lastBri = bri; _lastLabel = label;
            CurrentColor = hex;
            ChangeCount++;
            _errors?.Resolve("gv.send");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _errors?.Report("gv.send", ex.Message);
            return false;
        }
    }

    /// <summary>Zachte overgang in een paar stappen, nooit sneller dan de strip aankan.</summary>
    private async Task<bool> FadeAsync(IReadOnlyList<string> targets, string fromHex, string toHex, int bri, int minGap, string label, CancellationToken token)
    {
        var from = ColorUtil.ParseHex(fromHex);
        var to = ColorUtil.ParseHex(toHex);
        const int frames = 3;
        var ok = true;
        for (var f = 1; f <= frames; f++)
        {
            var c = ColorUtil.Lerp(from, to, f / (double)frames);
            ok &= await Send(targets, ColorUtil.ToHex(c.r, c.g, c.b), bri, label, token);
            if (f < frames) await Task.Delay(minGap, token);
        }
        CurrentColor = toHex;
        return ok;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(1000); } catch { }
        _cts?.Dispose();
    }
}
