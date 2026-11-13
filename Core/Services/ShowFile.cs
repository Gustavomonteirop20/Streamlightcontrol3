using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// Een lichtshow als bestand om te delen of te importeren. Kan een .json-bestand zijn, of een .png-afbeelding
/// waarin de show onzichtbaar is meegeschreven (tekstblok "slc-show" in de PNG). De afbeelding zelf laat de
/// kleuren van de show zien, zodat je kunt zien wat je importeert.
/// </summary>
public sealed class ShowFile
{
    public const string FormatName = "slc-show";
    public const string PngKeyword = "slc-show";

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    /// <summary>Optioneel: "song-…" als het de vervanging van een ingebouwde song-show is.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("song")] public string Song { get; set; } = "";
    [JsonPropertyName("bpm")] public double Bpm { get; set; }
    [JsonPropertyName("durationMs")] public int DurationMs { get; set; }
    /// <summary>Club-versie (snel). Verplicht.</summary>
    [JsonPropertyName("fast")] public string Fast { get; set; } = "";
    /// <summary>Veilige versie (±550 ms). Leeg = zelfde als <see cref="Fast"/>.</summary>
    [JsonPropertyName("safe")] public string Safe { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ShowFile FromJson(string json)
    {
        ShowFile? f;
        try { f = JsonSerializer.Deserialize<ShowFile>(json, Json); }
        catch (JsonException ex) { throw new FormatException("Dit is geen geldig lichtshow-bestand: " + ex.Message); }
        if (f == null || f.Format != FormatName) throw new FormatException("Dit is geen Stream Light Control-lichtshow.");
        if (string.IsNullOrWhiteSpace(f.Fast) && string.IsNullOrWhiteSpace(f.Safe)) throw new FormatException("De lichtshow heeft geen stappen.");
        if (string.IsNullOrWhiteSpace(f.Fast)) f.Fast = f.Safe;
        if (string.IsNullOrWhiteSpace(f.Safe)) f.Safe = f.Fast;
        if (string.IsNullOrWhiteSpace(f.Name)) f.Name = string.IsNullOrWhiteSpace(f.Song) ? "Geïmporteerde show" : "♪ " + f.Song;
        // Controleer dat de stappen echt te lezen zijn
        try { Presets.ParseData(f.Fast); Presets.ParseData(f.Safe); }
        catch (Exception ex) { throw new FormatException("De stappen in deze lichtshow zijn beschadigd: " + ex.Message); }
        return f;
    }

    public static ShowFile FromShow(LightShow show) => new()
    {
        Key = show.PresetKey.StartsWith("song-") ? show.PresetKey : "",
        Name = show.Name,
        Song = show.Name.TrimStart('♪', ' '),
        DurationMs = show.TotalDurationMs,
        Fast = show.HasTiers ? show.FastData : Presets.ToData(show.Steps),
        Safe = show.HasTiers ? show.SafeData : Presets.ToData(show.Steps)
    };

    /// <summary>Leest een .png (met ingebedde show) of .json.</summary>
    public static ShowFile Load(byte[] bytes)
    {
        if (IsPng(bytes))
        {
            var json = ExtractFromPng(bytes) ?? throw new FormatException("Deze afbeelding bevat geen lichtshow. Gebruik een afbeelding die door Stream Light Control (of Claude) is gemaakt.");
            return FromJson(json);
        }
        return FromJson(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
    }

    // ---------------------------------------------------------------- PNG

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool IsPng(byte[] b) => b.Length > 8 && b.AsSpan(0, 8).SequenceEqual(PngSignature);

    /// <summary>Schrijft de show als tEXt-blok in de PNG (vóór het eindblok). De afbeelding blijft gewoon te openen.</summary>
    public static byte[] EmbedInPng(byte[] png, string json)
    {
        if (!IsPng(png)) throw new ArgumentException("Geen PNG-afbeelding.");
        var text = Encoding.Latin1.GetBytes(PngKeyword).Concat(new byte[] { 0 })
            .Concat(Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)))).ToArray();
        var chunk = Chunk("tEXt", text);
        var iend = FindChunk(png, "IEND");
        if (iend < 0) throw new ArgumentException("Beschadigde PNG (geen IEND).");
        // Een eventueel oud show-blok eerst weghalen
        var clean = RemoveShowChunks(png);
        iend = FindChunk(clean, "IEND");
        return clean.Take(iend).Concat(chunk).Concat(clean.Skip(iend)).ToArray();
    }

    public static string? ExtractFromPng(byte[] png)
    {
        foreach (var (type, data, _, _) in Chunks(png))
        {
            if (type != "tEXt") continue;
            var zero = Array.IndexOf(data, (byte)0);
            if (zero < 0 || Encoding.Latin1.GetString(data, 0, zero) != PngKeyword) continue;
            var b64 = Encoding.ASCII.GetString(data, zero + 1, data.Length - zero - 1).Trim();
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch (FormatException) { return null; }
        }
        return null;
    }

    private static byte[] RemoveShowChunks(byte[] png)
    {
        var keep = new List<byte>(png.Take(8));
        foreach (var (type, data, start, length) in Chunks(png))
        {
            if (type == "tEXt")
            {
                var zero = Array.IndexOf(data, (byte)0);
                if (zero >= 0 && Encoding.Latin1.GetString(data, 0, zero) == PngKeyword) continue;
            }
            keep.AddRange(png.Skip(start).Take(length));
        }
        return keep.ToArray();
    }

    private static IEnumerable<(string type, byte[] data, int start, int length)> Chunks(byte[] png)
    {
        if (!IsPng(png)) yield break;
        var pos = 8;
        while (pos + 12 <= png.Length)
        {
            var len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            if (len < 0 || pos + 12 + len > png.Length) yield break;
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            yield return (type, png.AsSpan(pos + 8, len).ToArray(), pos, 12 + len);
            pos += 12 + len;
            if (type == "IEND") yield break;
        }
    }

    private static int FindChunk(byte[] png, string type)
    {
        foreach (var c in Chunks(png)) if (c.type == type) return c.start;
        return -1;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var t = Encoding.ASCII.GetBytes(type);
        var crc = Crc32(t.Concat(data).ToArray());
        return BigEndian(data.Length).Concat(t).Concat(data).Concat(BigEndian((int)crc)).ToArray();
    }

    private static byte[] BigEndian(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    // ---------------------------------------------------------------- toevoegen aan de app

    /// <summary>
    /// Voegt de show toe. Bestaat er al een show voor dit nummer (zelfde song-sleutel of naam), dan wordt die
    /// vervangen en blijven de koppelingen (gifts) gewoon werken.
    /// </summary>
    public LightShow? FindExisting(AppSettings s) =>
        (Key != "" ? s.Shows.FirstOrDefault(x => x.PresetKey == Key) : null)
        ?? s.Shows.FirstOrDefault(x => string.Equals(x.Name, Name, StringComparison.OrdinalIgnoreCase));

    public LightShow AddTo(AppSettings s, bool replaceExisting)
    {
        var fast = ShowTiers.IsFast(s.Lighting);
        var existing = replaceExisting ? FindExisting(s) : null;
        var show = existing ?? new LightShow();
        show.Name = existing?.IsPreset == true ? existing.Name : Name;
        show.Repeats = 1;
        show.FastData = Presets.ToData(Presets.ParseData(Fast));
        show.SafeData = Presets.ToData(Presets.ParseData(Safe));
        show.Steps = Presets.ParseData(fast ? Fast : Safe);
        if (existing == null)
        {
            if (s.Shows.Any(x => string.Equals(x.Name, show.Name, StringComparison.OrdinalIgnoreCase))) show.Name += " (import)";
            s.Shows.Add(show);
        }
        return show;
    }

    /// <summary>Koppelt de show aan een gift van precies dit aantal coins (gaat vóór de bredere coin-bereiken).</summary>
    public static EventTrigger LinkToCoins(AppSettings s, LightShow show, int coins)
    {
        var existing = s.Triggers.FirstOrDefault(t => t.EventType == EventTypes.Gift && t.MinCoins == coins && t.MaxCoins == coins);
        var trigger = existing ?? new EventTrigger { EventType = EventTypes.Gift, MinCoins = coins, MaxCoins = coins };
        trigger.Name = $"{show.Name} ({coins} coins)";
        trigger.ShowId = show.Id;
        trigger.Priority = Priorities.VeryHigh;
        trigger.Enabled = true;
        if (existing == null) s.Triggers.Add(trigger);
        return trigger;
    }
}
