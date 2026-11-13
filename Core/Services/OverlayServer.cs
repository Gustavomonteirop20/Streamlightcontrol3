using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// Kleine webserver, alleen op deze pc (127.0.0.1), voor de kerst-overlays. Zet de adressen als browser-/linkbron in
/// OBS of TikTok LIVE Studio. Pagina's vragen elke 0,7 s de stand op via /api/state.
/// </summary>
public sealed class OverlayServer : IDisposable
{
    private readonly Func<OverlayOptions> _options;
    private readonly DonationStats _stats;
    private readonly Action<string>? _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;

    public OverlayServer(DonationStats stats, Func<OverlayOptions> options, Action<string>? log = null)
    {
        _stats = stats; _options = options; _log = log;
    }

    public bool IsRunning => _listener != null;
    public int Port => _port;
    public string Status { get; private set; } = "Uit";
    public string BaseUrl => $"http://localhost:{_port}";

    public static readonly (string Path, string Name, string Size)[] Views =
    {
        ("alert", "Tipalert", "600 × 420"),
        ("doel", "Tipdoel", "520 × 200"),
        ("ranking", "Ranking", "420 × 480"),
        ("recent", "Recente tips", "420 × 420"),
        ("supporters", "Laatste supporter · Weekkampioen · Vandaag", "420 × 420"),
        ("alles", "Alles onder elkaar (staand)", "432 × 768"),
    };

    public void Apply()
    {
        var o = _options();
        if (!o.Enabled) { Stop(); Status = "Uit"; return; }
        if (IsRunning && _port == o.Port) return;
        Stop();
        try
        {
            var l = new TcpListener(IPAddress.Loopback, o.Port);
            l.Start();
            _listener = l; _port = o.Port;
            _cts = new CancellationTokenSource();
            _ = AcceptLoop(l, _cts.Token);
            Status = $"Aan · {BaseUrl}";
            _log?.Invoke($"[Overlays] gestart op {BaseUrl}");
        }
        catch (Exception ex)
        {
            _listener = null;
            Status = $"Kan poort {o.Port} niet openen (wordt al gebruikt?). Kies een andere poort.";
            _log?.Invoke($"[Overlays] starten mislukt: {ex.Message}");
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null; _cts = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener l, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(token); }
            catch { break; }
            _ = Task.Run(() => Serve(c, token));
        }
    }

    private async Task Serve(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
            using var stream = client.GetStream();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(10000);
            while (!cts.IsCancellationRequested)
            {
                var head = await ReadHead(stream, cts.Token);
                if (head == null) return;
                var line = head.Split("\r\n")[0].Split(' ');
                if (line.Length < 2) return;
                var keepAlive = !head.Contains("Connection: close", StringComparison.OrdinalIgnoreCase);
                var (status, type, body) = Route(line[0], line[1]);
                var hdr = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\n" +
                          $"Access-Control-Allow-Origin: *\r\nX-Content-Type-Options: nosniff\r\nConnection: {(keepAlive ? "keep-alive" : "close")}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr), cts.Token);
                await stream.WriteAsync(body, cts.Token);
                if (!keepAlive) return;
            }
        }
        catch { /* verbinding weg: niet erg */ }
    }

    private static async Task<string?> ReadHead(NetworkStream s, CancellationToken token)
    {
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (sb.Length < 8192)
        {
            var n = await s.ReadAsync(buf, token);
            if (n == 0) return null;
            sb.Append((char)buf[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') return sb.ToString();
        }
        return null;
    }

    /// <summary>Antwoord op één verzoek (los te testen).</summary>
    public (string Status, string Type, byte[] Body) Route(string method, string target)
    {
        if (method != "GET" && method != "HEAD") return ("405 Method Not Allowed", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Alleen GET"));
        var path = target.Split('?')[0].TrimEnd('/').ToLowerInvariant();
        if (path == "/api/state")
            return ("200 OK", "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(_stats.Snapshot(_options(), DateTime.UtcNow)));
        if (path is "" or "/kerst")
            return ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(OverlayPages.Index(Views.Select(v => (v.Path, v.Name, v.Size)))));
        if (path.StartsWith("/kerst/"))
        {
            var view = path["/kerst/".Length..];
            if (Views.Any(v => v.Path == view))
                return ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(OverlayPages.Page(view)));
        }
        return ("404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Niet gevonden"));
    }
}
