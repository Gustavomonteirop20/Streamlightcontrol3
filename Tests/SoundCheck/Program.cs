using StreamLightControl2;
using StreamLightControl2.Core.Services;

var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures");
var fails = 0;
foreach (var file in new[] { "synth-160bpm.mp3", "synth-160bpm.m4a" })
{
    try
    {
        var (mono, rate) = SoundDecoder.Decode(Path.Combine(dir, file));
        var a = ShowGenerator.Analyze(mono, rate);
        var show = ShowGenerator.Generate(a, file);
        var ok = Math.Abs(a.Bpm - 160) < 1.5 && a.Kicks.Count >= 40 && Math.Abs(show.TotalDurationMs - a.DurationMs) <= 5 && show.PlayExact;
        Console.WriteLine($"  {(ok ? "✔" : "✘")} {file}: {rate} Hz, {mono.Length / (double)rate:0.0} s, {a.Bpm} BPM, {a.Kicks.Count} kicks, {show.Steps.Count} stappen");
        if (!ok) fails++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✘ {file}: {ex.GetType().Name}: {ex.Message}");
        fails++;
    }
}
Console.WriteLine(fails == 0 ? "Sound lezen: alles goed" : $"Sound lezen: {fails} fout(en)");
return fails == 0 ? 0 : 1;
