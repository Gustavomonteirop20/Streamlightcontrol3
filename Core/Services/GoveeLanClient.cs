using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

public sealed class DiscoveredDevice
{
    public string Ip { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Mac { get; set; } = "";
    public override string ToString() => $"{(string.IsNullOrEmpty(Sku) ? "Govee" : Sku)} — {Ip}";
}

/// <summary>
/// Govee LAN API (officieel): zoeken via multicast 239.255.255.250:4001, antwoorden op poort 4002,
/// besturen via UDP 4003. Eén vaste luisteraar op 4002 vangt zowel zoekresultaten als statusantwoorden op.
/// </summary>
public sealed class GoveeLanClient : IDisposable
{
    public int ScanPort { get; init; } = 4001;
    public int ListenPort { get; init; } = 4002;
    public int ControlPort { get; init; } = 4003;
    public IPAddress MulticastAddress { get; init; } = IPAddress.Parse("239.255.255.250");
    /// <summary>Extra adressen voor het zoekbericht (wordt in tests gebruikt).</summary>
    public List<IPEndPoint> ExtraScanTargets { get; } = new();
    public bool UseMulticast { get; init; } = true;

    /// <summary>Pauze na elk commando zodat de lamp niets mist.</summary>
    public int CommandDelayMs { get; set; } = 80;

    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DeviceStatus>> _statusWaiters = new();
    private ConcurrentDictionary<string, DiscoveredDevice>? _scanResults;

    public bool ListenerActive => _listener != null;
    public string? ListenerError { get; private set; }

    /// <summary>Elk UDP-pakket dat wordt verstuurd (ip, json). Handig voor diagnose.</summary>
    public event Action<string, string>? CommandSent;

    public bool StartListener()
    {
        if (_listener != null) return true;
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, ListenPort));
            try { udp.JoinMulticastGroup(MulticastAddress); } catch { /* niet elk netwerk ondersteunt dit; unicast-antwoorden komen toch binnen */ }
            _listener = udp;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(udp, _cts.Token));
            ListenerError = null;
            return true;
        }
        catch (Exception ex)
        {
            ListenerError = ex.Message;
            return false;
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            try { HandlePacket(packet.Buffer, packet.RemoteEndPoint.Address.ToString()); }
            catch { /* onleesbaar pakket negeren */ }
        }
    }

    private void HandlePacket(byte[] buffer, string remoteIp)
    {
        using var doc = JsonDocument.Parse(buffer);
        if (!doc.RootElement.TryGetProperty("msg", out var msg)) return;
        var cmd = msg.TryGetProperty("cmd", out var c) ? c.GetString() : "";
        if (!msg.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        string Get(string k) => data.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

        if (cmd == "scan")
        {
            var ip = Get("ip");
            if (string.IsNullOrWhiteSpace(ip)) ip = remoteIp;
            _scanResults?.TryAdd(ip, new DiscoveredDevice { Ip = ip, Sku = Get("sku"), Mac = Get("device") });
        }
        else if (cmd == "devStatus")
        {
            int Int(string k) => data.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : 0;
            byte r = 0, g = 0, b = 0;
            if (data.TryGetProperty("color", out var col) && col.ValueKind == JsonValueKind.Object)
            {
                r = (byte)(col.TryGetProperty("r", out var rv) && rv.TryGetInt32(out var ri) ? Math.Clamp(ri, 0, 255) : 0);
                g = (byte)(col.TryGetProperty("g", out var gv) && gv.TryGetInt32(out var gi) ? Math.Clamp(gi, 0, 255) : 0);
                b = (byte)(col.TryGetProperty("b", out var bv) && bv.TryGetInt32(out var bi) ? Math.Clamp(bi, 0, 255) : 0);
            }
            var status = new DeviceStatus(Int("onOff") == 1, Math.Clamp(Int("brightness"), 0, 100), r, g, b);
            if (_statusWaiters.TryRemove(remoteIp, out var tcs)) tcs.TrySetResult(status);
        }
    }

    /// <summary>Zoekt alle Govee-apparaten met LAN Control op het lokale netwerk (alle modellen).</summary>
    public async Task<List<DiscoveredDevice>> DiscoverAsync(int waitMs = 3000, CancellationToken token = default)
    {
        if (!StartListener())
            throw new InvalidOperationException("Poort " + ListenPort + " is bezet: " + ListenerError);
        var results = new ConcurrentDictionary<string, DiscoveredDevice>();
        _scanResults = results;
        var scan = Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}");
        try
        {
            // Twee rondes, want UDP kan een pakket verliezen.
            for (var round = 0; round < 2 && !token.IsCancellationRequested; round++)
            {
                await SendScanAsync(scan);
                await Task.Delay(waitMs / 2, token);
            }
        }
        catch (OperationCanceledException) { }
        finally { _scanResults = null; }
        return results.Values.OrderBy(d => d.Ip).ToList();
    }

    private async Task SendScanAsync(byte[] scan)
    {
        var sent = false;
        if (UseMulticast)
        {
            var target = new IPEndPoint(MulticastAddress, ScanPort);
            // Via elke netwerkkaart versturen: voorkomt dat het zoekbericht via een VPN- of Hyper-V-adapter verdwijnt.
            foreach (var address in LocalIPv4Addresses())
            {
                try
                {
                    using var udp = new UdpClient(new IPEndPoint(address, 0));
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    await udp.SendAsync(scan, scan.Length, target);
                    sent = true;
                }
                catch { }
            }
            if (!sent)
            {
                try { using var udp = new UdpClient(); await udp.SendAsync(scan, scan.Length, target); }
                catch { }
            }
        }
        foreach (var extra in ExtraScanTargets)
        {
            try { using var udp = new UdpClient(); await udp.SendAsync(scan, scan.Length, extra); } catch { }
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { yield break; }
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { if (!nic.SupportsMulticast) continue; props = nic.GetIPProperties(); } catch { continue; }
            foreach (var ua in props.UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) yield return ua.Address;
        }
    }

    /// <summary>Vraagt aan/uit, helderheid en kleur op. Null = geen antwoord (lamp niet bereikbaar of LAN Control uit).</summary>
    public async Task<DeviceStatus?> GetStatusAsync(string ip, int timeoutMs = 1500, CancellationToken token = default)
    {
        if (!StartListener()) return null;
        var tcs = new TaskCompletionSource<DeviceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _statusWaiters[ip] = tcs;
        try
        {
            // Twee pogingen binnen de time-out.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await SendRawAsync(ip, "devStatus", new { }, token, delay: false);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs / 2, token));
                if (done == tcs.Task) return await tcs.Task;
            }
            return null;
        }
        catch (OperationCanceledException) { return null; }
        finally { _statusWaiters.TryRemove(new KeyValuePair<string, TaskCompletionSource<DeviceStatus>>(ip, tcs)); }
    }

    public Task SendCommandAsync(string ip, string command, object data, CancellationToken token = default) =>
        SendRawAsync(ip, command, data, token, delay: true);

    private async Task SendRawAsync(string ip, string command, object data, CancellationToken token, bool delay)
    {
        if (!IPAddress.TryParse(ip, out var address))
            throw new InvalidOperationException($"'{ip}' is geen geldig IP-adres.");
        var json = JsonSerializer.Serialize(new { msg = new { cmd = command, data } });
        var bytes = Encoding.UTF8.GetBytes(json);
        var gate = _locks.GetOrAdd(ip, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(address, ControlPort));
            CommandSent?.Invoke(ip, json);
            if (delay) await Task.Delay(CommandDelayMs, token);
        }
        finally { gate.Release(); }
    }

    public Task PowerAsync(string ip, bool on, CancellationToken token = default) =>
        SendCommandAsync(ip, "turn", new { value = on ? 1 : 0 }, token);

    public Task BrightnessAsync(string ip, int brightness, CancellationToken token = default) =>
        SendCommandAsync(ip, "brightness", new { value = Math.Clamp(brightness, 1, 100) }, token);

    public Task ColorAsync(string ip, byte r, byte g, byte b, CancellationToken token = default) =>
        SendCommandAsync(ip, "colorwc", new { color = new { r, g, b }, colorTemInKelvin = 0 }, token);

    // ---------------------------------------------------------------- Direct (Razer/DreamView-protocol)
    // Hetzelfde realtime-protocol dat Govee Desktop, Razer Chroma en OpenRGB gebruiken voor scherm- en
    // muzieksync. De kleur springt direct, zonder de ingebouwde overgang (fade) van colorwc.
    // Pakket: BB [lengte hi] [lengte lo] [commando] [data…] [XOR van alle voorgaande bytes], base64 in
    // {"msg":{"cmd":"razer","data":{"pt":"…"}}}. Na ±1 minuut zonder data valt de strip terug op zijn
    // eigen kleur, dus tijdens het afspelen wordt de modus af en toe opnieuw aangezet.

    /// <summary>Aantal kleurvakken dat we sturen (zelfde kleur overal = hele strip één kleur).</summary>
    public const int DirectLedCount = 20;

    public Task DirectModeAsync(string ip, bool on, CancellationToken token = default) =>
        SendCommandAsync(ip, "razer", new { pt = Convert.ToBase64String(DirectEnablePacket(on)) }, token);

    public Task DirectColorAsync(string ip, byte r, byte g, byte b, CancellationToken token = default) =>
        SendCommandAsync(ip, "razer", new { pt = Convert.ToBase64String(DirectColorPacket(r, g, b, DirectLedCount)) }, token);

    public static byte[] DirectEnablePacket(bool on) => WithChecksum(new byte[] { 0xBB, 0x00, 0x01, 0xB1, (byte)(on ? 1 : 0) });

    /// <summary>Elke zone een eigen kleur (Direct-modus). Zone 1 = begin van de strip (bij de controller).</summary>
    public Task DirectZonesAsync(string ip, IReadOnlyList<(byte r, byte g, byte b)> zones, CancellationToken token = default) =>
        SendCommandAsync(ip, "razer", new { pt = Convert.ToBase64String(DirectZonesPacket(zones)) }, token);

    public static byte[] DirectZonesPacket(IReadOnlyList<(byte r, byte g, byte b)> zones)
    {
        var count = Math.Clamp(zones.Count, 1, 255);
        var len = 2 + 3 * count;
        var p = new List<byte> { 0xBB, (byte)(len >> 8), (byte)(len & 0xFF), 0xB0, 0x01, (byte)count };
        for (var i = 0; i < count; i++) { var z = i < zones.Count ? zones[i] : ((byte)0, (byte)0, (byte)0); p.Add(z.Item1); p.Add(z.Item2); p.Add(z.Item3); }
        return WithChecksum(p.ToArray());
    }

    // ---------------------------------------------------------------- Segmenten (onofficieel, ptReal)
    // Niet door Govee beschreven, wel door de community: Bluetooth-pakketjes van 20 bytes via LAN.
    // 33 05 15 01 R G B 00 00 00 00 00 [masker laag] [masker hoog] 00 00 00 00 00 [XOR van byte 0–18]
    // Bit n van het masker = segment n+1. Max. 12 pakketjes per bericht; berichten 50 ms uit elkaar.

    public static byte[] SegmentColorFrame(byte r, byte g, byte b, int mask)
    {
        var f = new byte[20];
        f[0] = 0x33; f[1] = 0x05; f[2] = 0x15; f[3] = 0x01;
        f[4] = r; f[5] = g; f[6] = b;
        f[12] = (byte)(mask & 0xFF); f[13] = (byte)((mask >> 8) & 0xFF);
        byte x = 0;
        for (var i = 0; i < 19; i++) x ^= f[i];
        f[19] = x;
        return f;
    }

    /// <summary>Per segment een kleur via ptReal. Segmenten met dezelfde kleur gaan samen in één pakketje.</summary>
    public async Task SegmentColorsAsync(string ip, IReadOnlyList<(byte r, byte g, byte b)> segments, CancellationToken token = default)
    {
        var frames = segments.Select((c, i) => (c, i)).Where(x => x.i < 16)
            .GroupBy(x => x.c).Select(g => SegmentColorFrame(g.Key.Item1, g.Key.Item2, g.Key.Item3, g.Aggregate(0, (m, x) => m | (1 << x.i))))
            .Select(Convert.ToBase64String).ToList();
        for (var k = 0; k < frames.Count; k += 12)
        {
            if (k > 0) await Task.Delay(50, token);
            await SendCommandAsync(ip, "ptReal", new { command = frames.Skip(k).Take(12).ToArray() }, token);
        }
    }

    public static byte[] DirectColorPacket(byte r, byte g, byte b, int count)
    {
        count = Math.Clamp(count, 1, 255);
        var len = 2 + 3 * count;                       // gradient-uit + aantal + RGB × aantal
        var p = new List<byte> { 0xBB, (byte)(len >> 8), (byte)(len & 0xFF), 0xB0, 0x01, (byte)count };
        for (var i = 0; i < count; i++) { p.Add(r); p.Add(g); p.Add(b); }
        return WithChecksum(p.ToArray());
    }

    private static byte[] WithChecksum(byte[] body)
    {
        byte x = 0;
        foreach (var v in body) x ^= v;
        var p = new byte[body.Length + 1];
        body.CopyTo(p, 0);
        p[^1] = x;
        return p;
    }

    /// <summary>
    /// Kleur op een deel van de strip (RGBIC-segmenten) via het ptReal-commando: een Govee BLE-pakket over LAN.
    /// Niet in de officiële LAN-documentatie; strips die het niet kennen negeren het.
    /// </summary>
    public Task SegmentColorAsync(string ip, byte r, byte g, byte b, int segmentMask, CancellationToken token = default) =>
        SendCommandAsync(ip, "ptReal", new { command = new[] { Convert.ToBase64String(SegmentPacket(r, g, b, segmentMask)) } }, token);

    public static byte[] SegmentPacket(byte r, byte g, byte b, int segmentMask)
    {
        var p = new byte[20];
        p[0] = 0x33; p[1] = 0x05; p[2] = 0x15; p[3] = 0x01;
        p[4] = r; p[5] = g; p[6] = b;
        p[12] = (byte)(segmentMask & 0xFF);
        p[13] = (byte)((segmentMask >> 8) & 0xFF);
        p[14] = (byte)((segmentMask >> 16) & 0xFF);
        byte x = 0;
        for (var i = 0; i < 19; i++) x ^= p[i];
        p[19] = x;
        return p;
    }

    /// <summary>Bitmasker voor de tweede helft van een strip met <paramref name="segments"/> segmenten.</summary>
    public static int SecondHalfMask(int segments)
    {
        var mask = 0;
        for (var i = segments / 2; i < segments; i++) mask |= 1 << i;
        return mask;
    }

    public static (byte r, byte g, byte b) ParseHex(string hex) => ColorUtil.ParseHex(hex);

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }
}
