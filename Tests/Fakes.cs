using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace StreamLightControl2.Tests;

/// <summary>Nagebootste Govee-lamp die het officiële LAN-protocol spreekt (scan, devStatus, turn, brightness, colorwc).</summary>
public sealed class FakeGoveeDevice : IDisposable
{
    private readonly UdpClient _control;
    private readonly UdpClient _scan;
    private readonly int _replyPort;
    private readonly CancellationTokenSource _cts = new();
    public ConcurrentQueue<(DateTime at, string cmd, JsonElement data)> Commands { get; } = new();
    public bool On { get; private set; } = true;
    public int Brightness { get; private set; } = 40;
    public (int r, int g, int b) Color { get; private set; } = (255, 255, 255);
    public bool RespondToStatus { get; set; } = true;
    public string Mac { get; set; } = "AA:BB:CC:DD:EE:FF:00:11";
    /// <summary>Alle Direct-beelden: kleur per zone (zone 1 = index 0).</summary>
    public List<string[]> ZoneFrames { get; } = new();
    /// <summary>Realtime-modus (razer-protocol) staat aan.</summary>
    public bool DirectOn { get; private set; }
    /// <summary>Kent de strip het razer-protocol? (zo niet: negeren, zoals een oude strip doet)</summary>
    public bool SupportsDirect { get; set; } = true;
    public int BadPackets { get; private set; }
    /// <summary>Kleuren die in directe modus binnenkwamen.</summary>
    public List<string> DirectColors { get; } = new();

    public FakeGoveeDevice(int scanPort, int controlPort, int replyPort, string ip = "127.0.0.1")
    {
        _replyPort = replyPort;
        _control = new UdpClient(new IPEndPoint(IPAddress.Parse(ip), controlPort));
        _scan = new UdpClient(new IPEndPoint(IPAddress.Parse(ip), scanPort));
        _ = Loop(_control, false);
        _ = Loop(_scan, true);
    }

    private async Task Loop(UdpClient udp, bool isScan)
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(_cts.Token); } catch { return; }
            using var doc = JsonDocument.Parse(r.Buffer);
            var msg = doc.RootElement.GetProperty("msg");
            var cmd = msg.GetProperty("cmd").GetString()!;
            var data = msg.GetProperty("data").Clone();
            Commands.Enqueue((DateTime.Now, cmd, data));
            switch (cmd)
            {
                case "scan":
                    await Reply(new { msg = new { cmd = "scan", data = new { ip = "127.0.0.1", device = Mac, sku = "H619E", bleVersionHard = "3.01.01", wifiVersionSoft = "1.02.03" } } });
                    break;
                case "devStatus":
                    if (RespondToStatus)
                        await Reply(new { msg = new { cmd = "devStatus", data = new { onOff = On ? 1 : 0, brightness = Brightness, color = new { r = Color.r, g = Color.g, b = Color.b }, colorTemInKelvin = 0 } } });
                    break;
                case "turn": On = data.GetProperty("value").GetInt32() == 1; break;
                case "brightness": Brightness = data.GetProperty("value").GetInt32(); break;
                case "colorwc":
                    if (DirectOn) break;    // net als de echte strip: in realtime-modus telt alleen razer-data
                    var c = data.GetProperty("color");
                    Color = (c.GetProperty("r").GetInt32(), c.GetProperty("g").GetInt32(), c.GetProperty("b").GetInt32());
                    break;
                case "razer":
                    if (!SupportsDirect) break;
                    var p = Convert.FromBase64String(data.GetProperty("pt").GetString()!);
                    byte x = 0;
                    for (var i = 0; i < p.Length - 1; i++) x ^= p[i];
                    var len = (p[1] << 8) | p[2];
                    if (p[0] != 0xBB || x != p[^1] || len != p.Length - 5) { BadPackets++; break; }
                    if (p[3] == 0xB1) DirectOn = p[4] == 1;
                    else if (p[3] == 0xB0 && DirectOn)
                    {
                        var count = p[5];
                        if (count * 3 + 2 != len) { BadPackets++; break; }
                        Color = (p[6], p[7], p[8]);
                        lock (DirectColors) DirectColors.Add($"#{p[6]:X2}{p[7]:X2}{p[8]:X2}");
                        var zf = new string[count];
                        for (var z = 0; z < count; z++) zf[z] = $"#{p[6 + 3 * z]:X2}{p[7 + 3 * z]:X2}{p[8 + 3 * z]:X2}";
                        lock (ZoneFrames) ZoneFrames.Add(zf);
                    }
                    break;
            }
        }
    }

    private async Task Reply(object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var u = new UdpClient();
        await u.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, _replyPort));
    }

    public List<string> ColorHistory() => Commands.Where(c => c.cmd == "colorwc")
        .Select(c => { var col = c.data.GetProperty("color"); return $"#{col.GetProperty("r").GetInt32():X2}{col.GetProperty("g").GetInt32():X2}{col.GetProperty("b").GetInt32():X2}"; })
        .ToList();

    public void Reset() { while (Commands.TryDequeue(out _)) { } lock (DirectColors) DirectColors.Clear(); }

    public void Dispose()
    {
        _cts.Cancel();
        _control.Dispose();
        _scan.Dispose();
    }
}

/// <summary>Nagebootste BetterTok WebSocket-server (Local of Cloud).</summary>
public sealed class FakeBetterTokServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private WebSocket? _current;
    public int Connections;
    public string? LastAuthorization;
    /// <summary>Cloud-gedrag: controleer de Bearer-key.</summary>
    public string? RequiredKey { get; set; }
    public bool SendConnectedFrame { get; set; }
    public ConcurrentQueue<string> Received { get; } = new();

    public FakeBetterTokServer(int port)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
            LastAuthorization = ctx.Request.Headers["Authorization"];
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            var ws = wsCtx.WebSocket;
            Interlocked.Increment(ref Connections);
            if (RequiredKey != null && LastAuthorization != "Bearer " + RequiredKey)
            {
                await SendTo(ws, "{\"type\":\"error\",\"code\":\"UNAUTHORIZED\",\"message\":\"Invalid API key\"}");
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", CancellationToken.None);
                continue;
            }
            _current = ws;
            if (SendConnectedFrame) await SendTo(ws, "{\"type\":\"connected\",\"username\":\"mhstreams\",\"events\":[\"gift\",\"follow\"]}");
            _ = ReadLoop(ws);
        }
    }

    private async Task ReadLoop(WebSocket ws)
    {
        var buf = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return;
                var text = Encoding.UTF8.GetString(buf, 0, r.Count);
                Received.Enqueue(text);
                if (text.Contains("\"ping\"")) await SendTo(ws, "{\"type\":\"pong\"}");
            }
        }
        catch { }
    }

    private static Task SendTo(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    public async Task SendAsync(string json)
    {
        for (var i = 0; i < 50 && (_current == null || _current.State != WebSocketState.Open); i++) await Task.Delay(50);
        await SendTo(_current!, json);
    }

    public async Task DropConnectionAsync()
    {
        var ws = _current;
        _current = null;
        if (ws != null) await ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "server restart", CancellationToken.None);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}

/// <summary>Doet alsof Spotify een nummer speelt (in te stellen vanuit de test).</summary>
public sealed class FakeNowPlaying : StreamLightControl2.Core.Services.INowPlayingSource
{
    public volatile StreamLightControl2.Core.Services.PlayerSnapshot Current = StreamLightControl2.Core.Services.PlayerSnapshot.NotRunning;
    public StreamLightControl2.Core.Services.PlayerSnapshot Read() => Current;
    public void Play(string artist, string title) =>
        Current = new(StreamLightControl2.Core.Services.PlayerState.Playing, new StreamLightControl2.Core.Services.NowPlaying(artist, title));
    public void Pause() => Current = new(StreamLightControl2.Core.Services.PlayerState.Paused, null);
}

/// <summary>Geluidsniveau zoals een uptempo-nummer: een kick op elke beat, daartussen zachter geluid.</summary>
public sealed class FakeAudio : StreamLightControl2.Core.Services.IAudioLevelSource
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Random _rnd = new(7);
    public DateTime StartedAt { get; } = DateTime.Now;
    /// <summary>Hoe ver (ms) een moment na de laatste kick valt.</summary>
    public double PhaseOf(DateTime at) => ((at - StartedAt).TotalMilliseconds % (60000.0 / Bpm) + 60000.0 / Bpm) % (60000.0 / Bpm);
    public double Bpm { get; set; } = 150;
    public bool Silent { get; set; }
    public bool Broken { get; set; }
    public float? ReadPeak()
    {
        if (Broken) return null;
        if (Silent) return 0f;
        var beatMs = 60000.0 / Bpm;
        var phase = _clock.ElapsedMilliseconds % beatMs;
        var kick = phase < 70 ? 0.95 * (1 - phase / 140) : 0;
        return (float)Math.Max(kick, 0.18 + _rnd.NextDouble() * 0.08);
    }
    public void Dispose() { }
}

/// <summary>Nep-DonateWave: geeft per verzoek het volgende antwoord uit de lijst (laatste blijft herhalen).</summary>
public sealed class FakeDonateWave : System.Net.Http.HttpMessageHandler
{
    public readonly List<(int Status, string Body)> Responses = new();
    public readonly List<(string Url, string? Auth, bool HasOrigin)> Requests = new();
    private int _i;
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Headers.Contains("Origin")));
        (int, string) r;
        lock (Responses) r = Responses.Count == 0 ? (200, "{\"events\":[],\"next_cursor\":\"\",\"poll_after_seconds\":2}") : Responses[Math.Min(_i++, Responses.Count - 1)];
        return Task.FromResult(new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)r.Item1) { Content = new System.Net.Http.StringContent(r.Item2) });
    }
    public static string Tip(string cursor, string id, int cents, DateTime createdUtc, string type = "tip.received", string name = "Supporter", bool test = false, bool special = false, bool wheel = false) =>
        "{\"events\":[{\"cursor\":\"" + cursor + "\",\"id\":\"" + id + "\",\"type\":\"" + type + "\",\"created_at\":\"" + createdUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") +
        "\",\"expires_at\":\"" + createdUtc.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",\"data\":{\"tip_id\":\"t" + id + "\",\"amount_cents\":" + cents +
        ",\"currency\":\"eur\",\"display_name\":\"" + name + "\",\"special\":" + (special ? "true" : "false") + ",\"lucky_wheel\":" + (wheel ? "true" : "false") + ",\"test\":" + (test ? "true" : "false") + ",\"onbekend_veld\":1}}],\"next_cursor\":\"" + cursor + "\",\"poll_after_seconds\":2,\"retention_days\":7}";
}
