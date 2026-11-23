using System.IO;
using NAudio.Wave;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2;

/// <summary>Leest een sound (mp3, wav, m4a, mp4, aac, wma) als mono samples voor de showmaker.</summary>
public static class SoundDecoder
{
    public const string Filter = "Sound of video|*.mp3;*.wav;*.m4a;*.mp4;*.aac;*.wma|Alle bestanden|*.*";
    public static readonly string[] Extensions = { ".mp3", ".wav", ".m4a", ".mp4", ".aac", ".wma" };
    private const int MaxSeconds = 15 * 60;

    public static bool IsSound(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static (float[] Mono, int SampleRate) Decode(string path)
    {
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try { return WavReader.Read(File.ReadAllBytes(path)); }
            catch (InvalidDataException) { /* bijzonder .wav-formaat: via Windows proberen */ }
        }
        Exception? first = null;
        try
        {
            using var reader = new MediaFoundationReader(path);
            return ReadAll(reader);
        }
        catch (Exception ex) { first = ex; }
        if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            try { using var mp3 = new Mp3FileReader(path); return ReadAll(mp3); }
            catch { /* hieronder de eerste fout melden */ }
        }
        throw new InvalidDataException("Windows kan deze sound niet lezen (" + first?.Message + "). Probeer een .mp3 of .wav.");
    }

    private static (float[] Mono, int SampleRate) ReadAll(WaveStream stream)
    {
        var sp = stream.ToSampleProvider();
        var ch = sp.WaveFormat.Channels; var rate = sp.WaveFormat.SampleRate;
        var max = (long)rate * MaxSeconds;
        var mono = new List<float>(rate * 60);
        var buf = new float[rate * ch];
        int read;
        while ((read = sp.Read(buf, 0, buf.Length)) > 0 && mono.Count < max)
            for (var i = 0; i + ch <= read; i += ch)
            {
                float s = 0; for (var c = 0; c < ch; c++) s += buf[i + c];
                mono.Add(s / ch);
            }
        return (mono.ToArray(), rate);
    }
}
