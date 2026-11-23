using System.Collections.ObjectModel;
using System.IO;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>Kleurenset voor de showmaker: hoofdkleur, pomp (accent op de maat) en de kleur van de zware drop.</summary>
public sealed record GeneratorPalette(string Key, string Name, string Main, string Pump, string Heavy);

/// <summary>Wat de showmaker in een sound hoort.</summary>
public sealed record SoundAnalysis(double Bpm, double BeatMs, long FirstBeatMs, IReadOnlyList<long> Kicks,
    IReadOnlyList<BarKind> Bars, long MusicStartMs, long DurationMs);

/// <summary>Soort maat: stil, rustig, groove (met hi-hats), melodie (zonder hats), drop (zwaarste bas), opbouw (vlak voor een drop).</summary>
public enum BarKind { Silent, Break, Groove, Melodic, Drop, Build }

/// <summary>
/// Showmaker: maakt van een sound (mp3/wav/…) een lichtshow in de stijl van Nick z'n video (gemeten op die video):
///   · groove (kicks + hi-hats): per maat een donkere tik → amber → rood;
///   · melodie (zonder hi-hats): rood dat op elke kick pompt;
///   · opbouw (bas valt weg vlak voor de drop): rood ↔ gedimd op elke beat, laatste beat donker;
///   · drop (zwaarste bas): blauw met een donkere tik op de beat en om de maat amber tussen het blauw.
/// Alles op de gemeten kicks, geen wit, hooguit 2 donkere tikken per seconde. Speelt precies af (PlayExact).
/// </summary>
public static class ShowGenerator
{
    public static IReadOnlyList<GeneratorPalette> Palettes { get; } = new[]
    {
        new GeneratorPalette("nick", "Rood/amber + blauw (zoals Nick z'n video)", "#FF1400", "#FF8A1E", "#1E32FF"),
        new GeneratorPalette("neon", "Roze/paars + cyaan", "#FF00CC", "#8A00FF", "#00E5FF"),
        new GeneratorPalette("toxic", "Groen/geel + paars", "#00FF3C", "#FFE600", "#8A00FF"),
    };

    public const int TickMs = 70;          // donkere tik op de beat
    public const int MaxDarkPerSecond = 2;
    private const int Rate = 12000;        // analyse-samplerate (5 ms = precies 60 samples, geen afrondingsdrift)
    private const int HopMs = 5;

    // ------------------------------------------------------------------ analyse

    public static SoundAnalysis Analyze(float[] mono, int sampleRate)
    {
        if (sampleRate <= 0 || mono.Length < sampleRate / 2) throw new InvalidDataException("De sound is te kort (minder dan een halve seconde).");
        var x = Resample(mono, sampleRate);
        var durationMs = (long)(mono.Length * 1000.0 / sampleRate);
        var hop = Rate * HopMs / 1000;
        var n = x.Length / hop;

        // Lage band (± 40–150 Hz): kick drum
        var lo = Band(x, 40, 150);
        var hi = HighPass(x);
        var envLo = new double[n];
        var envAll = new double[n];
        var envHi = new double[n];
        for (var i = 0; i < n; i++)
        {
            double s1 = 0, s2 = 0, s3 = 0;
            for (var j = i * hop; j < (i + 1) * hop; j++) { s1 += lo[j] * lo[j]; s2 += x[j] * x[j]; s3 += hi[j] * hi[j]; }
            envLo[i] = Math.Sqrt(s1 / hop); envAll[i] = Math.Sqrt(s2 / hop); envHi[i] = Math.Sqrt(s3 / hop);
        }
        var maxAll = envAll.DefaultIfEmpty(0).Max();
        if (maxAll < 1e-4) throw new InvalidDataException("De sound is stil.");

        // Muziek begint: eerste moment boven 10% van het maximum
        var start = Array.FindIndex(envAll, v => v > maxAll * 0.10);
        var musicStartMs = Math.Max(0, start) * (long)HopMs;

        // Kicks: pieken in de lage band boven een meebewegende drempel, minimaal 220 ms uit elkaar
        var maxLo = envLo.Max();
        var kicks = new List<long>();
        var peaks = new List<double>();
        var win = 1000 / HopMs;
        var sorted = new double[2 * win + 1];
        for (var i = 1; i < n - 1; i++)
        {
            if (envLo[i] < maxLo * 0.22 || envLo[i] < envLo[i - 1] || envLo[i] < envLo[i + 1]) continue;
            var a = Math.Max(0, i - win); var b = Math.Min(n, i + win + 1);
            var local = 0.0; for (var j = a; j < b; j++) local += envLo[j]; local /= b - a;
            if (envLo[i] < local * 1.35) continue;
            // echte piek binnen ±60 ms
            var peak = true;
            for (var j = Math.Max(0, i - 12); j < Math.Min(n, i + 13); j++) if (envLo[j] > envLo[i]) { peak = false; break; }
            if (!peak) continue;
            // moment van de klap = waar de bas omhoog schiet (halverwege naar de piek), niet de piek zelf
            var on = i;
            while (on > 0 && i - on < 12 && envLo[on - 1] > envLo[i] * 0.5) on--;
            var t = (long)on * HopMs;
            if (kicks.Count > 0 && t - kicks[^1] < 220) { if (envLo[i] > peaks[^1]) { kicks[^1] = t; peaks[^1] = envLo[i]; } continue; }
            kicks.Add(t); peaks.Add(envLo[i]);
        }

        // Tempo: autocorrelatie van de aanzetten (60–200 BPM), daarna in het bereik 90–180
        var onset = new double[n];
        for (var i = 1; i < n; i++) onset[i] = Math.Max(0, envLo[i] - envLo[i - 1]) + 0.3 * Math.Max(0, envAll[i] - envAll[i - 1]);
        var mean = onset.Average();
        for (var i = 0; i < n; i++) onset[i] -= mean;
        double bestLag = 500, bestVal = double.MinValue;
        for (var lagMs = 300; lagMs <= 1000; lagMs += HopMs)
        {
            var lag = lagMs / HopMs; double sum = 0;
            for (var i = 0; i + lag < n; i++) sum += onset[i] * onset[i + lag];
            sum /= Math.Max(1, n - lag);
            if (sum > bestVal) { bestVal = sum; bestLag = lagMs; }
        }
        // verfijnen met de kick-afstanden (mediaan van afstanden rond de beat)
        var gaps = kicks.Zip(kicks.Skip(1), (p, q) => (double)(q - p)).Where(g => Math.Abs(g - bestLag) < bestLag * 0.12).OrderBy(g => g).ToList();
        var beat = gaps.Count >= 8 ? gaps[gaps.Count / 2] : bestLag;
        var bpm = 60000.0 / beat;
        while (bpm < 90) { bpm *= 2; beat /= 2; }
        while (bpm > 180) { bpm /= 2; beat *= 2; }

        // Fase: rooster dat het best op de kicks valt
        long firstBeat = musicStartMs;
        if (kicks.Count > 0)
        {
            var bestScore = -1.0;
            foreach (var k in kicks.Take(64))
            {
                var phase = k % (long)Math.Round(beat);
                var score = kicks.Sum(q => { var d = Math.Abs(((q - phase) % beat + beat) % beat); d = Math.Min(d, beat - d); return d < 50 ? 1 - d / 50 : 0; });
                if (score > bestScore) { bestScore = score; firstBeat = phase; }
            }
        }

        // Maten van 4 beats: energie en kicks per maat
        var barMs = beat * 4;
        var bars = new List<(double all, double low, double hats, int kicks)>();
        for (var t = (double)firstBeat; t < durationMs; t += barMs)
        {
            var a = (int)(t / HopMs); var b = Math.Min(n, (int)((t + barMs) / HopMs));
            if (b <= a) break;
            double sa = 0, sl = 0, sh = 0; for (var i = a; i < b; i++) { sa += envAll[i]; sl += envLo[i]; sh += envHi[i]; }
            var kc = kicks.Count(k => k >= t && k < t + barMs);
            bars.Add((sa / (b - a), sl / (b - a), sh / Math.Max(1e-9, sa), kc));
        }
        var p90All = Percentile(bars.Select(b => b.all), 0.9);
        var p90Low = Percentile(bars.Select(b => b.low), 0.9);
        var loud = bars.Where(b => b.all >= p90All * 0.15).ToList();
        var medLow = Percentile(loud.Select(b => b.low), 0.5);
        var dropLine = Math.Max(p90Low * 0.75, medLow * 1.35);         // duidelijk zwaardere bas dan normaal
        var hatsLine = Percentile(loud.Where(b => b.low < dropLine).Select(b => b.hats), 0.5);
        var kinds = bars.Select(b =>
            b.all < p90All * 0.15 ? BarKind.Silent
            : b.kicks >= 3 && b.low >= dropLine ? BarKind.Drop
            : b.low < medLow * 0.6 ? BarKind.Break
            : b.hats >= hatsLine * 0.97 ? BarKind.Groove
            : BarKind.Melodic).ToList();
        // Losse uitschieters gladstrijken (één maat anders tussen twee gelijke)
        for (var i = 1; i < kinds.Count - 1; i++)
            if (kinds[i - 1] == kinds[i + 1] && kinds[i] != kinds[i - 1] && kinds[i] != BarKind.Silent) kinds[i] = kinds[i - 1];
        // Opbouw: de maat vlak voor een drop
        for (var i = 1; i < kinds.Count; i++)
            if (kinds[i] == BarKind.Drop && kinds[i - 1] is BarKind.Break or BarKind.Groove or BarKind.Melodic) kinds[i - 1] = BarKind.Build;

        return new SoundAnalysis(Math.Round(bpm, 2), beat, firstBeat, kicks, kinds, musicStartMs, durationMs);
    }

    // ------------------------------------------------------------------ show maken

    public static LightShow Generate(float[] mono, int sampleRate, string name, string paletteKey = "nick") =>
        Generate(Analyze(mono, sampleRate), name, paletteKey);

    public static LightShow Generate(SoundAnalysis a, string name, string paletteKey = "nick")
    {
        var pal = Palettes.FirstOrDefault(p => p.Key == paletteKey) ?? Palettes[0];
        var fast = Steps(a, pal);
        var safe = SafeSteps(a, pal);
        var show = new LightShow
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Nieuwe show" : name.Trim(),
            Repeats = 1,
            PlayExact = true,
            Steps = new ObservableCollection<LightStep>(fast.Select(s => s.Clone())),
            FastData = Presets.ToData(fast),
            SafeData = Presets.ToData(safe),
        };
        return show;
    }

    /// <summary>De snelle (precieze) versie: elke stap op de kicks.</summary>
    private static List<LightStep> Steps(SoundAnalysis a, GeneratorPalette pal)
    {
        var raw = new List<(long at, string hex, int bri, string? b, bool off)>();
        void Add(double at, string hex, int bri = 100, string? b = null, bool off = false) => raw.Add(((long)Math.Round(at), hex, bri, b, off));
        var beat = a.BeatMs; var barMs = beat * 4;
        // Kick in de buurt van een rooster-moment: precies daarop
        double Snap(double t)
        {
            var best = t; var bd = 45.0;
            foreach (var k in a.Kicks) { var d = Math.Abs(k - t); if (d < bd) { bd = d; best = k; } if (k > t + 50) break; }
            return best;
        }
        Add(0, pal.Pump, 25);   // vóór de muziek: zacht amber
        var dropCount = 0;
        for (var i = 0; i < a.Bars.Count; i++)
        {
            var t0 = a.FirstBeatMs + i * barMs;
            var kind = a.Bars[i];
            switch (kind)
            {
                case BarKind.Silent:
                    Add(Snap(t0), pal.Pump, 25);
                    break;
                case BarKind.Break:
                    Add(Snap(t0), pal.Pump, 40);
                    break;
                case BarKind.Drop:
                    // zware drop: blauw met een donkere tik op beat 1 en 3; om de maat amber tussen het blauw op beat 3
                    dropCount++;
                    for (var bt = 0; bt < 4; bt += 2)
                    {
                        var t = Snap(t0 + bt * beat);
                        Add(t, "#000000", 1, off: true);
                        var split = dropCount % 2 == 0 && bt == 2;
                        Add(t + TickMs, pal.Heavy, 100, split ? pal.Pump : null);
                        if (split) Add(t + beat, pal.Heavy);
                    }
                    break;
                case BarKind.Groove:
                {
                    // zoals de video: donkere tik → amber (± 1,4 beat) → rood
                    var t = Snap(t0);
                    Add(t, "#000000", 1, off: true);
                    Add(t + Math.Min(140, beat * 0.35), pal.Pump);
                    Add(Snap(t0 + beat) + beat * 0.4, pal.Main);
                    break;
                }
                case BarKind.Melodic:
                    // rood dat op elke kick pompt
                    for (var bt = 0; bt < 4; bt++) Add(Snap(t0 + bt * beat), pal.Main, bt % 2 == 0 ? 100 : 78);
                    break;
                case BarKind.Build:
                    // vlak voor de drop: op elke beat rood ↔ gedimd, laatste beat donker
                    for (var bt = 0; bt < 4; bt++)
                    {
                        var t = Snap(t0 + bt * beat);
                        if (bt == 3) { Add(t, "#000000", 1, off: true); continue; }
                        Add(t, pal.Main);
                        Add(t + beat * 0.55, pal.Main, 20);
                    }
                    break;
            }
        }
        Add(a.DurationMs, "#000000", 1, off: true);   // eindpunt

        // Op volgorde, dubbelen en te korte stukjes weg
        raw = raw.Where(r => r.at >= 0 && r.at <= a.DurationMs).OrderBy(r => r.at).ToList();
        var steps = new List<LightStep>();
        var darkAt = new List<long>();
        for (var i = 0; i < raw.Count - 1; i++)
        {
            var r = raw[i];
            var dur = (int)(raw[i + 1].at - r.at);
            if (dur <= 0) continue;
            var off = r.off;
            // hooguit 2 donkere tikken per seconde: anders gedimd in de vorige kleur
            if (off && darkAt.Count(d => r.at - d < 1000) >= MaxDarkPerSecond) off = false;
            LightStep st;
            if (r.off && !off)
            {
                var prev = steps.LastOrDefault(s => s.Kind == StepKind.Color);
                st = LightStep.Color(prev?.ColorHex ?? pal.Main, 1, 20);
            }
            else if (off) { st = LightStep.Off(1); darkAt.Add(r.at); }
            else st = r.b != null ? LightStep.Split(r.hex, r.b, 1, r.bri) : LightStep.Color(r.hex, 1, r.bri);
            st.DurationMs = Math.Min(60000, dur);
            // korter dan 60 ms: bij de vorige stap optellen
            if (dur < 60 && steps.Count > 0) { steps[^1].DurationMs = Math.Min(60000, steps[^1].DurationMs + dur); continue; }
            if (steps.Count > 0 && Same(steps[^1], st) && steps[^1].DurationMs + dur <= 60000) { steps[^1].DurationMs += dur; continue; }
            // lange stappen (> 60 s) opknippen
            var left = dur;
            while (left > 0) { var part = st.Clone(); part.DurationMs = Math.Max(50, Math.Min(60000, left)); steps.Add(part); left -= part.DurationMs; }
        }
        if (steps.Count == 0) steps.Add(LightStep.Color(pal.Pump, (int)Math.Clamp(a.DurationMs, 50, 60000), 25));
        // laatste stap tot het einde (stappen moeten samen even lang zijn als de sound)
        var total = steps.Sum(s => (long)s.DurationMs);
        if (total < a.DurationMs) steps[^1].DurationMs = (int)Math.Min(60000, steps[^1].DurationMs + (a.DurationMs - total));
        return steps;
    }

    /// <summary>Veilige versie voor strips met fade: per stuk één kleur, nooit korter dan 550 ms.</summary>
    private static List<LightStep> SafeSteps(SoundAnalysis a, GeneratorPalette pal)
    {
        var steps = new List<LightStep>();
        var barMs = a.BeatMs * 4;
        void Add(string hex, int bri, double ms, string? b = null)
        {
            var d = (int)Math.Round(ms);
            if (d <= 0) return;
            if (steps.Count > 0 && steps[^1].ColorHex == hex.ToUpperInvariant() && steps[^1].Brightness == bri && steps[^1].DurationMs + d <= 60000) { steps[^1].DurationMs += d; return; }
            var st = b != null ? LightStep.Split(hex, b, Math.Max(50, Math.Min(60000, d)), bri) : LightStep.Color(hex, Math.Max(50, Math.Min(60000, d)), bri, 1, 150);
            steps.Add(st);
        }
        Add(pal.Pump, 25, a.FirstBeatMs);
        for (var i = 0; i < a.Bars.Count; i++)
        {
            var len = Math.Min(barMs, a.DurationMs - (a.FirstBeatMs + i * barMs));
            switch (a.Bars[i])
            {
                case BarKind.Silent: Add(pal.Pump, 25, len); break;
                case BarKind.Break: Add(pal.Pump, 40, len); break;
                case BarKind.Groove: Add(pal.Pump, 100, len / 2); Add(pal.Main, 100, len - len / 2); break;
                case BarKind.Melodic: Add(pal.Main, 100, len); break;
                case BarKind.Drop: Add(pal.Heavy, 100, len); break;
                case BarKind.Build: Add(pal.Main, 60, len); break;
            }
        }
        // korter dan 550 ms samenvoegen met de vorige
        var merged = new List<LightStep>();
        foreach (var s in steps)
        {
            if (merged.Count > 0 && (s.DurationMs < 550 || merged[^1].DurationMs < 550) && merged[^1].DurationMs + s.DurationMs <= 60000) { merged[^1].DurationMs += s.DurationMs; continue; }
            merged.Add(s);
        }
        var total = merged.Sum(s => (long)s.DurationMs);
        if (merged.Count == 0) merged.Add(LightStep.Color(pal.Pump, (int)Math.Clamp(a.DurationMs, 550, 60000), 25));
        else if (total < a.DurationMs) merged[^1].DurationMs = (int)Math.Min(60000, merged[^1].DurationMs + (a.DurationMs - total));
        return merged;
    }

    private static bool Same(LightStep a, LightStep b) =>
        a.Kind == b.Kind && (a.Kind == StepKind.Off || (a.ColorHex == b.ColorHex && a.Brightness == b.Brightness && a.ColorHex2 == b.ColorHex2 && !a.IsSplit && !b.IsSplit));

    // ------------------------------------------------------------------ hulpjes

    private static float[] Resample(float[] x, int sr)
    {
        if (sr == Rate) return x;
        var n = (int)((long)x.Length * Rate / sr);
        var y = new float[n];
        var ratio = sr / (double)Rate;
        for (var i = 0; i < n; i++)
        {
            // gemiddelde over het venster (simpel anti-aliasfilter)
            var a = (int)(i * ratio); var b = Math.Min(x.Length, Math.Max(a + 1, (int)((i + 1) * ratio)));
            double s = 0; for (var j = a; j < b; j++) s += x[j];
            y[i] = (float)(s / (b - a));
        }
        return y;
    }

    /// <summary>Hi-hats en ander hoog getik: tweede verschil (versterkt hoge tonen, dempt bas en melodie sterk).</summary>
    private static double[] HighPass(float[] x)
    {
        var y = new double[x.Length];
        for (var i = 2; i < x.Length; i++) y[i] = x[i] - 2.0 * x[i - 1] + x[i - 2];
        return y;
    }

    /// <summary>Bandfilter (twee keer laagdoorlaat min laagdoorlaat).</summary>
    private static double[] Band(float[] x, double fLow, double fHigh)
    {
        double ah = Math.Exp(-2 * Math.PI * fHigh / Rate), al = Math.Exp(-2 * Math.PI * fLow / Rate);
        var y = new double[x.Length];
        double h1 = 0, h2 = 0, l1 = 0, l2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            h1 = (1 - ah) * x[i] + ah * h1; h2 = (1 - ah) * h1 + ah * h2;
            l1 = (1 - al) * x[i] + al * l1; l2 = (1 - al) * l1 + al * l2;
            y[i] = h2 - l2;
        }
        return y;
    }

    private static double Percentile(IEnumerable<double> values, double p)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v[Math.Clamp((int)(p * (v.Count - 1)), 0, v.Count - 1)];
    }
}

/// <summary>Leest een .wav-bestand (PCM 8/16/24/32-bit of float) als mono samples.</summary>
public static class WavReader
{
    public static (float[] Mono, int SampleRate) Read(byte[] data)
    {
        using var br = new BinaryReader(new MemoryStream(data));
        if (data.Length < 12 || new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Geen geldig .wav-bestand.");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Geen geldig .wav-bestand.");
        int format = 0, channels = 0, rate = 0, bits = 0;
        byte[]? pcm = null;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var id = new string(br.ReadChars(4)); var size = br.ReadInt32();
            if (size < 0 || br.BaseStream.Position + size > br.BaseStream.Length) size = (int)(br.BaseStream.Length - br.BaseStream.Position);
            if (id == "fmt ")
            {
                var fmt = br.ReadBytes(size);
                format = BitConverter.ToUInt16(fmt, 0); channels = BitConverter.ToUInt16(fmt, 2);
                rate = BitConverter.ToInt32(fmt, 4); bits = BitConverter.ToUInt16(fmt, 14);
                if (format == 0xFFFE && fmt.Length >= 26) format = BitConverter.ToUInt16(fmt, 24);   // WAVE_FORMAT_EXTENSIBLE
            }
            else if (id == "data") { pcm = br.ReadBytes(size); }
            else br.BaseStream.Seek(size, SeekOrigin.Current);
            if (size % 2 == 1 && br.BaseStream.Position < br.BaseStream.Length) br.ReadByte();
        }
        if (pcm == null || channels <= 0 || rate <= 0) throw new InvalidDataException("Het .wav-bestand heeft geen geluid.");
        if (!(format == 1 && bits is 8 or 16 or 24 or 32) && !(format == 3 && bits == 32))
            throw new InvalidDataException($"Dit .wav-formaat wordt niet ondersteund (formaat {format}, {bits} bit).");
        var bps = bits / 8; var frames = pcm.Length / (bps * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = (f * channels + c) * bps;
                sum += format == 3 ? BitConverter.ToSingle(pcm, o) : bits switch
                {
                    8 => (pcm[o] - 128) / 128.0,
                    16 => BitConverter.ToInt16(pcm, o) / 32768.0,
                    24 => ((pcm[o] | (pcm[o + 1] << 8) | (pcm[o + 2] << 16)) << 8 >> 8) / 8388608.0,
                    _ => BitConverter.ToInt32(pcm, o) / 2147483648.0
                };
            }
            mono[f] = (float)(sum / channels);
        }
        return (mono, rate);
    }
}
