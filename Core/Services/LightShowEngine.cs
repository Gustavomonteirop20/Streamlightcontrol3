using System.Diagnostics;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>Wat er op dit moment naar de lampen gaat. De preview in de app toont precies dit.</summary>
public sealed record LightFrame(string ShowId, string ShowName, int StepIndex, int Repeat, string ColorHex, int Brightness, bool On, double Progress);

/// <summary>Toestand van de lampen vóór een show, om daarna te herstellen.</summary>
public sealed class Baseline
{
    public Dictionary<string, DeviceStatus> States { get; } = new();
}

/// <summary>
/// Speelt lichtshows echt af via de Govee LAN-client: per stap kleur, helderheid, flitsen en overgangen.
/// Alle apparaten krijgen elk commando tegelijk (parallel).
/// </summary>
public sealed class LightShowEngine
{
    private readonly GoveeLanClient _govee;
    private readonly Func<LightingOptions> _options;
    private readonly Dictionary<string, (bool on, int bri, string hex)> _known = new();
    /// <summary>Per lamp: wanneer de directe modus (realtime-protocol) voor het laatst is aangezet.</summary>
    private readonly Dictionary<string, long> _directSince = new();
    private readonly object _gate = new();

    public LightShowEngine(GoveeLanClient govee, Func<LightingOptions> options)
    {
        _govee = govee;
        _options = options;
    }

    /// <summary>Per lamp (IP) de zones die op stream te zien zijn, of null. Ingevuld door de controller.</summary>
    public Func<string, ZoneSetup?>? Zones { get; set; }

    public event Action<LightFrame>? Frame;
    public event Action<string?>? Finished;   // showId of null

    /// <param name="startOffsetMs">Begin zoveel ms ín de show (de sound loopt al, bijv. door de 2 s polling van DonateWave).</param>
    public async Task PlayAsync(LightShow show, IReadOnlyList<string> ips, CancellationToken token, int startOffsetMs = 0)
    {
        if (show.Steps.Count == 0) return;
        var opt = _options();
        if (opt.CalmShows) { await PlayCalmAsync(show, ips, token, startOffsetMs, opt); return; }
        _govee.CommandDelayMs = opt.EffectiveCommandDelayMs;
        // De lamp kan intussen via de Govee-app zijn veranderd: begin elke show met een schone lei.
        ForgetState();
        var total = Math.Max(1, show.TotalDurationMs);
        var clock = Stopwatch.StartNew();
        (byte r, byte g, byte b) current = (0, 0, 0);
        var firstColor = true;
        var black = opt.FlashMode == FlashMode.Black;
        // Snelheid: de lamp krijgt één keer de hoogste helderheid van de show. Lagere helderheden per stap
        // worden in de kleur verrekend. Zo kost elke kleurwissel maar één UDP-commando.
        var maxBri = Math.Max(1, show.Steps.Where(s => s.Kind == StepKind.Color).Select(s => s.Brightness).DefaultIfEmpty(100).Max());
        // Korter dan dit kan de strip een flitsfase niet tonen
        var minPhase = opt.DirectColor ? opt.EffectiveCommandDelayMs + 15 : opt.CommandDelayMs + 25;
        // Strobe-veiligheid: hooguit 3 flitsen per seconde (instelbaar), voor kijkers die gevoelig zijn voor flitsen
        var minFlashCycle = opt.LimitStrobes ? 333 : 0;
        // Zone-effecten op de stukjes strip die op stream te zien zijn
        var cozy = Presets.CozySongs.Contains(show.PresetKey);
        var zoneStep = 0;
        await ForAll(ips, ip => EnsureOnAndBrightness(ip, maxBri, token));
        // "Licht eerder sturen": het hele schema schuift naar voren om wifi/strip-vertraging op te vangen.
        long planned = -opt.LatencyMs - Math.Max(0, startOffsetMs);
        // Snelheidslimiet: wissels die sneller komen dan de strip aankan worden overgeslagen (schema blijft op de beat).
        long lastSend = long.MinValue / 2;
        long lastWhite = long.MinValue / 2;
        // Vergelijkt geplande momenten (het beat-schema), niet de klok: opstartvertraging telt niet mee.
        bool Allowed(long plannedAt)
        {
            if (plannedAt - lastSend < opt.MinChangeMs * 0.8) return false; // 20% marge zodat beats net onder de grens niet wegvallen
            lastSend = plannedAt;
            return true;
        }
        try
        {
            for (var rep = 0; rep < show.Repeats; rep++)
            {
                for (var i = 0; i < show.Steps.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var step = show.Steps[i];
                    // Vast schema: elke stap begint op zijn geplande moment, vertraging stapelt niet op.
                    var stepStart = planned;
                    planned += step.DurationMs;
                    // Show begint midden in (sound loopt al): stappen die al voorbij zijn overslaan
                    if (rep == 0 && planned <= -opt.LatencyMs) continue;
                    double Progress() => Math.Min(1, clock.ElapsedMilliseconds / (double)total);

                    if (step.Kind == StepKind.Off)
                    {
                        Emit(show, i, rep, "#000000", 0, false, Progress());
                        if (!Allowed(stepStart)) { }
                        else if (black) await ForAll(ips, ip => SetColor(ip, (0, 0, 0), token));
                        else await ForAll(ips, ip => SetPower(ip, false, token));
                        current = (0, 0, 0);
                        await HoldUntil(clock, stepStart + step.DurationMs, token);
                        continue;
                    }

                    // Helderheid shows (camera-vriendelijk): alles naar verhouding lager, kleuren blijven gelijk
                    var master = opt.ShowBrightness / 100.0;
                    var target = Scale(ColorUtil.ParseHex(step.ColorHex), step.Brightness / (double)maxBri * master);
                    var targetHex = ColorUtil.ToHex(target.r, target.g, target.b);
                    if (!black) await ForAll(ips, ip => EnsureOn(ip, token));

                    // Strip A/B: bij 2+ lampen krijgt elke lamp zijn eigen kleur (hele strip één kleur).
                    // Bij 1 lamp alleen helft/helft als segmenten aan staan; anders gewoon kleur A.
                    if (step.IsSplit && (ips.Count >= 2 || opt.SegmentEffects))
                    {
                        // Helft/helft: 2+ lampen → afwisselend kleur A/B per lamp; 1 lamp → segmenten.
                        var second = Scale(ColorUtil.ParseHex(step.ColorHex2), step.Brightness / (double)maxBri * master);
                        Emit(show, i, rep, targetHex, step.Brightness, true, Progress());
                        if (Allowed(stepStart)) await SplitAsync(ips, target, second, opt, token);
                        current = target;
                        firstColor = false;
                        await HoldUntil(clock, stepStart + step.DurationMs, token);
                        continue;
                    }

                    var flashes = Math.Min(step.Flashes, Math.Max(1, step.DurationMs / (2 * minPhase)));
                    if (minFlashCycle > 0) flashes = Math.Min(flashes, Math.Max(1, step.DurationMs / minFlashCycle));
                    if (flashes <= 1)
                    {
                        var fade = firstColor ? 0 : Math.Min(step.TransitionMs, step.DurationMs);
                        if (fade > 0)
                        {
                            // Ledstrips verwerken maar een paar commando's per seconde: overgangen in rustige stappen
                            var frameMs = Math.Max(opt.CommandDelayMs * 2, 180);
                            var frames = Math.Max(1, fade / frameMs);
                            var from = current;
                            for (var f = 1; f <= frames; f++)
                            {
                                token.ThrowIfCancellationRequested();
                                var c = ColorUtil.Lerp(from, target, f / (double)frames);
                                Emit(show, i, rep, ColorUtil.ToHex(c.r, c.g, c.b), step.Brightness, true, Progress());
                                var frameAt = stepStart + (long)(fade * ((f - 1) / (double)frames));
                                if (Allowed(frameAt) || f == frames)
                                {
                                    lastSend = frameAt;
                                    if (f == frames) { var pat = zoneStep / 2 % ZoneEffects.Patterns; await ForAll(ips, ip => PaintAsync(ip, c, pat, 0, 1, cozy, opt, token)); }
                                    else await ForAll(ips, ip => SetColor(ip, c, token));
                                }
                                await HoldUntil(clock, stepStart + (long)(fade * (f / (double)frames)), token);
                            }
                        }
                        else
                        {
                            // Strobe-veiligheid: witte flitsen die sneller dan 3 per seconde komen worden overgeslagen
                            var white = step.ColorHex == "#FFFFFF";
                            var skipStrobe = opt.LimitStrobes && white && stepStart - lastWhite < 333;
                            if (white && !skipStrobe) lastWhite = stepStart;
                            Emit(show, i, rep, targetHex, step.Brightness, true, Progress());
                            if (!skipStrobe && Allowed(stepStart))
                            {
                                var pat = zoneStep / 2 % ZoneEffects.Patterns;
                                await ForAll(ips, ip => ZoneStepAsync(ip, target, pat, stepStart, step.DurationMs, clock, cozy, opt, token));
                            }
                        }
                        if (step.ColorHex != "#FFFFFF") zoneStep++;
                        current = target;
                        await HoldUntil(clock, stepStart + step.DurationMs, token);
                    }
                    else
                    {
                        var cycle = step.DurationMs / (double)flashes;
                        for (var f = 0; f < flashes; f++)
                        {
                            token.ThrowIfCancellationRequested();
                            var cycleStart = stepStart + (long)(cycle * f);
                            Emit(show, i, rep, targetHex, step.Brightness, true, Progress());
                            if (Allowed(cycleStart)) await ForAll(ips, ip => FlashOn(ip, target, step.Brightness, opt.FlashMode, token));
                            await HoldUntil(clock, cycleStart + (long)(cycle * 0.55), token);
                            var last = f == flashes - 1 && rep == show.Repeats - 1 && i == show.Steps.Count - 1;
                            if (last) break; // laatste flits van de show: kleur laten staan
                            Emit(show, i, rep, "#000000", step.Brightness, false, Progress());
                            if (Allowed(cycleStart + (long)(cycle * 0.55))) await ForAll(ips, ip => FlashOff(ip, opt.FlashMode, token));
                            await HoldUntil(clock, cycleStart + (long)cycle, token);
                        }
                        current = (0, 0, 0);
                        await HoldUntil(clock, stepStart + step.DurationMs, token);
                    }
                    firstColor = false;
                }
            }
        }
        finally
        {
            Finished?.Invoke(show.Id);
        }
    }

    /// <summary>
    /// Rustige show (minder knipperen): per sectie één kleur, de zichtbare zones bewegen op de beat,
    /// de rest van de strip is een rustige gloed. Zonder zones: alleen per sectie een kleurwissel.
    /// </summary>
    private async Task PlayCalmAsync(LightShow show, IReadOnlyList<string> ips, CancellationToken token, int startOffsetMs, LightingOptions opt)
    {
        _govee.CommandDelayMs = opt.EffectiveCommandDelayMs;
        ForgetState();
        var cozy = Presets.CozySongs.Contains(show.PresetKey);
        var frames = CalmShow.Build(show, cozy);
        var total = Math.Max(1, show.TotalDurationMs);
        var master = opt.ShowBrightness / 100.0;
        var clock = Stopwatch.StartNew();
        await ForAll(ips, ip => EnsureOnAndBrightness(ip, 100, token));
        var lastBase = new System.Collections.Concurrent.ConcurrentDictionary<string, (byte, byte, byte)>();
        try
        {
            for (var rep = 0; rep < show.Repeats; rep++)
            {
                long offset = (long)rep * total - opt.LatencyMs - Math.Max(0, startOffsetMs);
                foreach (var f in frames)
                {
                    token.ThrowIfCancellationRequested();
                    var at = offset + f.AtMs;
                    if (rep == 0 && at + f.DurationMs <= -opt.LatencyMs) continue;   // al voorbij (show begint midden in)
                    await HoldUntil(clock, at, token);
                    var color = Scale(f.Color, master);
                    var frame = f with { Color = color };
                    Frame?.Invoke(new LightFrame(show.Id, show.Name, 0, rep, ColorUtil.ToHex(color.r, color.g, color.b), 100, true,
                        Math.Min(1, (f.AtMs + rep * (long)total) / (double)(total * Math.Max(1, show.Repeats)))));
                    await ForAll(ips, async ip =>
                    {
                        var z = ZonesFor(ip, opt);
                        if (z != null) await SendZonesAsync(ip, z, CalmShow.Render(z, frame, cozy), color, token);
                        else if (!lastBase.TryGetValue(ip, out var lb) || lb != color)
                        {
                            lastBase[ip] = color;
                            await SetColor(ip, color, token);
                        }
                    });
                }
                await HoldUntil(clock, offset + total, token);
            }
        }
        finally
        {
            Finished?.Invoke(show.Id);
        }
    }

    private ZoneSetup? ZonesFor(string ip, LightingOptions opt)
    {
        if (!opt.ZoneEffects) return null;
        var z = Zones?.Invoke(ip);
        return z is { Visible.Count: > 0 } ? z : null;
    }

    /// <summary>Eén stap met zone-effect: bewegende patronen krijgen meerdere beelden binnen de stap.</summary>
    private async Task ZoneStepAsync(string ip, (byte r, byte g, byte b) target, int pattern, long stepStart, int durationMs,
        Stopwatch clock, bool cozy, LightingOptions opt, CancellationToken token)
    {
        var z = ZonesFor(ip, opt);
        if (z == null) { await SetColor(ip, target, token); return; }
        var frameMin = z.Segment ? Math.Max(opt.MinChangeMs, 400) : Math.Max(opt.MinChangeMs, 90);
        var frames = ZoneEffects.FramesFor(pattern, durationMs, frameMin, z.Visible.Count, cozy);
        for (var f = 0; f < frames; f++)
        {
            if (f > 0) await HoldUntil(clock, stepStart + (long)(durationMs * (f / (double)frames)), token);
            await SendZonesAsync(ip, z, ZoneEffects.Render(z, target, pattern, f, frames, cozy), target, token);
        }
    }

    /// <summary>Kleur met zone-effect (één beeld), of gewoon de hele strip als er geen zones zijn.</summary>
    private Task PaintAsync(string ip, (byte r, byte g, byte b) c, int pattern, int frame, int frames, bool cozy, LightingOptions opt, CancellationToken token)
    {
        var z = ZonesFor(ip, opt);
        return z == null ? SetColor(ip, c, token) : SendZonesAsync(ip, z, ZoneEffects.Render(z, c, pattern, frame, frames, cozy), c, token);
    }

    private async Task SendZonesAsync(string ip, ZoneSetup z, IReadOnlyList<(byte r, byte g, byte b)> zones, (byte r, byte g, byte b) main, CancellationToken token)
    {
        long since;
        bool known;
        lock (_gate) known = _directSince.TryGetValue(ip, out since);
        if (!z.Segment)
        {
            if (!known || Environment.TickCount64 - since > 20_000)
            {
                await _govee.DirectModeAsync(ip, true, token);
                lock (_gate) _directSince[ip] = Environment.TickCount64;
            }
            await _govee.DirectZonesAsync(ip, zones, token);
        }
        else
        {
            if (known)
            {
                await _govee.DirectModeAsync(ip, false, token);
                lock (_gate) _directSince.Remove(ip);
            }
            await _govee.SegmentColorsAsync(ip, zones, token);
        }
        Remember(ip, hex: ColorUtil.ToHex(main.r, main.g, main.b));
    }

    private void Emit(LightShow show, int step, int rep, string hex, int bri, bool on, double progress) =>
        Frame?.Invoke(new LightFrame(show.Id, show.Name, step, rep, hex, bri, on, progress));

    private static async Task HoldUntil(Stopwatch clock, long targetMs, CancellationToken token)
    {
        var wait = targetMs - clock.ElapsedMilliseconds;
        if (wait > 0) await Task.Delay((int)wait, token);
    }

    private static Task ForAll(IReadOnlyList<string> ips, Func<string, Task> action) =>
        ips.Count == 1 ? action(ips[0]) : Task.WhenAll(ips.Select(action));

    // ------- commando's met bijhouden van de bekende toestand (geen dubbele commando's) -------

    private (bool on, int bri, string hex) Known(string ip)
    {
        lock (_gate) return _known.TryGetValue(ip, out var k) ? k : (false, -1, "");
    }

    private void Remember(string ip, bool? on = null, int? bri = null, string? hex = null)
    {
        lock (_gate)
        {
            var k = _known.TryGetValue(ip, out var x) ? x : (false, -1, "");
            _known[ip] = (on ?? k.Item1, bri ?? k.Item2, hex ?? k.Item3);
        }
    }

    public void ForgetState() { lock (_gate) _known.Clear(); }

    private async Task SetPower(string ip, bool on, CancellationToken token)
    {
        await _govee.PowerAsync(ip, on, token);
        Remember(ip, on: on);
    }

    private async Task SplitAsync(IReadOnlyList<string> ips, (byte r, byte g, byte b) a, (byte r, byte g, byte b) b, LightingOptions opt, CancellationToken token)
    {
        if (ips.Count >= 2)
        {
            await Task.WhenAll(ips.Select((ip, idx) => SetColor(ip, idx % 2 == 0 ? a : b, token)));
            return;
        }
        if (ips.Count == 0) return;
        var only = ips[0];
        await SetColor(only, a, token);
        if (opt.SegmentEffects)
            await _govee.SegmentColorAsync(only, b.r, b.g, b.b, GoveeLanClient.SecondHalfMask(opt.SegmentCount), token);
    }

    /// <summary>Test voor Device Manager: hele strip kleur A, tweede helft kleur B.</summary>
    public async Task TestSplitAsync(IReadOnlyList<string> ips, string a, string b)
    {
        var opt = _options();
        _govee.CommandDelayMs = opt.CommandDelayMs;
        foreach (var ip in ips) await EnsureOnAndBrightness(ip, 100, CancellationToken.None);
        var force = new LightingOptions { SegmentEffects = true, SegmentCount = opt.SegmentCount, CommandDelayMs = opt.CommandDelayMs };
        await SplitAsync(ips, ColorUtil.ParseHex(a), ColorUtil.ParseHex(b), force, CancellationToken.None);
        Frame?.Invoke(new LightFrame("", "Helft/helft-test", 0, 0, a, 100, true, 1));
    }

    private static (byte r, byte g, byte b) Scale((byte r, byte g, byte b) c, double f)
    {
        f = Math.Clamp(f, 0, 1);
        return ((byte)Math.Round(c.r * f), (byte)Math.Round(c.g * f), (byte)Math.Round(c.b * f));
    }

    private async Task EnsureOn(string ip, CancellationToken token)
    {
        if (!Known(ip).on) await SetPower(ip, true, token);
    }

    private async Task EnsureOnAndBrightness(string ip, int brightness, CancellationToken token)
    {
        var k = Known(ip);
        if (!k.on) await SetPower(ip, true, token);
        if (k.bri != brightness)
        {
            await _govee.BrightnessAsync(ip, brightness, token);
            Remember(ip, bri: brightness);
        }
    }

    /// <param name="persistent">Kleur die moet blijven staan (vaste kleur, herstellen): altijd via het
    /// standaardcommando, want de directe modus valt na een minuut zonder data terug.</param>
    private async Task SetColor(string ip, (byte r, byte g, byte b) c, CancellationToken token, bool persistent = false)
    {
        var direct = _options().DirectColor;
        long since;
        bool known;
        lock (_gate) known = _directSince.TryGetValue(ip, out since);
        if (direct && !persistent)
        {
            // Directe modus aanzetten (en elke 20 s opnieuw, anders valt de strip terug op zijn eigen kleur)
            if (!known || Environment.TickCount64 - since > 20_000)
            {
                await _govee.DirectModeAsync(ip, true, token);
                lock (_gate) _directSince[ip] = Environment.TickCount64;
            }
            await _govee.DirectColorAsync(ip, c.r, c.g, c.b, token);
            Remember(ip, hex: ColorUtil.ToHex(c.r, c.g, c.b));
            return;
        }
        if (known || direct)
        {
            // Uit de directe modus, anders negeert de strip het standaardcommando
            await _govee.DirectModeAsync(ip, false, token);
            lock (_gate) _directSince.Remove(ip);
        }
        await _govee.ColorAsync(ip, c.r, c.g, c.b, token);
        Remember(ip, hex: ColorUtil.ToHex(c.r, c.g, c.b));
    }

    private async Task FlashOn(string ip, (byte r, byte g, byte b) c, int bri, FlashMode mode, CancellationToken token)
    {
        if (mode == FlashMode.PowerToggle && !Known(ip).on) await SetPower(ip, true, token);
        await SetColor(ip, c, token);
    }

    private Task FlashOff(string ip, FlashMode mode, CancellationToken token) =>
        mode == FlashMode.PowerToggle ? SetPower(ip, false, token) : SetColor(ip, (0, 0, 0), token);

    // ------- direct bedienen (Quick Actions / Device Manager) -------

    public async Task SetStaticAsync(IReadOnlyList<string> ips, string hex, int brightness, CancellationToken token = default)
    {
        _govee.CommandDelayMs = _options().EffectiveCommandDelayMs;
        var c = ColorUtil.ParseHex(hex);
        await ForAll(ips, async ip =>
        {
            await SetPower(ip, true, token);
            await _govee.BrightnessAsync(ip, brightness, token);
            Remember(ip, bri: brightness);
            await SetColor(ip, c, token, persistent: true);
        });
        Frame?.Invoke(new LightFrame("", "Vaste kleur", 0, 0, ColorUtil.ToHex(c.r, c.g, c.b), brightness, true, 1));
    }

    /// <summary>
    /// Live kleur (Spotify-modus): lamp op 100% helderheid, de gewenste helderheid zit in de kleur verrekend.
    /// Zo kost elke wissel één UDP-commando.
    /// </summary>
    public async Task LiveColorAsync(IReadOnlyList<string> ips, string hex, int brightness, string label, CancellationToken token = default)
        => await LiveColorAsync(ips, hex, null, brightness, label, token);

    /// <summary>Live kleur met optioneel een tweede kleur voor strip B (bij 2+ lampen: om-en-om per lamp).</summary>
    public async Task LiveColorAsync(IReadOnlyList<string> ips, string hex, string? hexB, int brightness, string label, CancellationToken token = default)
    {
        if (hexB != null && ips.Count >= 2)
        {
            _govee.CommandDelayMs = _options().EffectiveCommandDelayMs;
            var f = brightness / 100.0 * (_options().ShowBrightness / 100.0);
            var a = Scale(ColorUtil.ParseHex(hex), f); var b = Scale(ColorUtil.ParseHex(hexB), f);
            await Task.WhenAll(ips.Select(async (ip, idx) =>
            {
                await EnsureOnAndBrightness(ip, 100, token);
                await SetColor(ip, idx % 2 == 0 ? a : b, token);
            }));
            Frame?.Invoke(new LightFrame("", label, 0, 0, ColorUtil.ToHex(a.r, a.g, a.b), brightness, true, 1));
            return;
        }
        _govee.CommandDelayMs = _options().EffectiveCommandDelayMs;
        var c = Scale(ColorUtil.ParseHex(hex), brightness / 100.0 * (_options().ShowBrightness / 100.0));
        await ForAll(ips, async ip =>
        {
            await EnsureOnAndBrightness(ip, 100, token);
            await SetColor(ip, c, token);
        });
        Frame?.Invoke(new LightFrame("", label, 0, 0, ColorUtil.ToHex(c.r, c.g, c.b), brightness, true, 1));
    }

    /// <summary>Losse kleur voor tests (snelheidstest): via dezelfde weg als een lichtshow (direct of standaard).</summary>
    public Task TestColorAsync(string ip, byte r, byte g, byte b, CancellationToken token = default)
    {
        _govee.CommandDelayMs = _options().EffectiveCommandDelayMs;
        return SetColor(ip, (r, g, b), token);
    }

    public async Task PowerAllAsync(IReadOnlyList<string> ips, bool on, CancellationToken token = default)
    {
        await ForAll(ips, ip => SetPower(ip, on, token));
        Frame?.Invoke(new LightFrame("", on ? "Aan" : "Uit", 0, 0, on ? Known(ips.FirstOrDefault() ?? "").hex : "#000000", 100, on, 1));
    }

    public async Task BrightnessAllAsync(IReadOnlyList<string> ips, int brightness, CancellationToken token = default)
    {
        await ForAll(ips, async ip => { await _govee.BrightnessAsync(ip, brightness, token); Remember(ip, bri: brightness); });
    }

    // ------- herstellen na een show -------

    public async Task<Baseline> CaptureAsync(IReadOnlyList<string> ips)
    {
        var baseline = new Baseline();
        var opt = _options();
        if (opt.RestoreMode != RestoreMode.Previous) return baseline;
        var results = await Task.WhenAll(ips.Select(async ip => (ip, status: await _govee.GetStatusAsync(ip, 900))));
        foreach (var (ip, status) in results)
        {
            if (status == null) continue;
            baseline.States[ip] = status;
            Remember(ip, status.On, status.Brightness, status.ColorHex);
        }
        return baseline;
    }

    public async Task RestoreAsync(Baseline? baseline, IReadOnlyList<string> ips)
    {
        var opt = _options();
        try
        {
            switch (opt.RestoreMode)
            {
                case RestoreMode.None:
                    return;
                case RestoreMode.FixedColor:
                    await SetStaticAsync(ips, opt.RestoreColor, opt.RestoreBrightness);
                    return;
                case RestoreMode.Previous:
                    if (baseline == null) return;
                    await ForAll(ips, async ip =>
                    {
                        if (!baseline.States.TryGetValue(ip, out var s)) return;
                        if (s.On)
                        {
                            await SetPower(ip, true, CancellationToken.None);
                            await _govee.BrightnessAsync(ip, Math.Max(1, s.Brightness));
                            Remember(ip, bri: s.Brightness);
                            if (s.R + s.G + s.B > 0) await SetColor(ip, (s.R, s.G, s.B), CancellationToken.None, persistent: true);
                        }
                        else await SetPower(ip, false, CancellationToken.None);
                    });
                    var first = baseline.States.Values.FirstOrDefault();
                    if (first != null) Frame?.Invoke(new LightFrame("", "Hersteld", 0, 0, first.On ? first.ColorHex : "#000000", first.Brightness, first.On, 1));
                    return;
            }
        }
        catch { /* herstellen is best effort */ }
    }
}
