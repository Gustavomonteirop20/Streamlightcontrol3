using System.IO;
using System.Net;
using System.Net.Security;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    private readonly Func<X509Certificate2?>? _cert;
    private TcpListener? _listener, _tlsListener;
    private CancellationTokenSource? _cts;
    private int _port;

    public OverlayServer(DonationStats stats, Func<OverlayOptions> options, Action<string>? log = null, Func<X509Certificate2?>? certificate = null)
    {
        _stats = stats; _options = options; _log = log; _cert = certificate;
    }

    /// <summary>https draait (poort + 1) met het eigen certificaat.</summary>
    public bool TlsRunning => _tlsListener != null;
    public int TlsPort => _port + 1;

    /// <summary>https-adres met echte domeinnaam, voor TikTok LIVE Studio.</summary>
    public static string TikTokUrl(int port, string view) => $"https://{OverlayCert.Host}:{port + 1}/kerst/{view}.html";

    public bool IsRunning => _listener != null;
    public int Port => _port;
    public string Status { get; private set; } = "Uit";
    public string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>Adres voor OBS / TikTok LIVE Studio (met punten, eindigt op .html: zo accepteert TikTok het).</summary>
    public static string ViewUrl(int port, string view) => $"http://127.0.0.1:{port}/kerst/{view}.html";

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
            _ = AcceptLoop(l, null, _cts.Token);
            Status = $"Aan · {BaseUrl}";
            _log?.Invoke($"[Overlays] gestart op {BaseUrl}");
            StartTls(o.Port + 1, _cts.Token);
        }
        catch (Exception ex)
        {
            _listener = null;
            Status = $"Kan poort {o.Port} niet openen (wordt al gebruikt?). Kies een andere poort.";
            _log?.Invoke($"[Overlays] starten mislukt: {ex.Message}");
        }
    }

    private void StartTls(int port, CancellationToken token)
    {
        X509Certificate2? cert;
        try { cert = _cert?.Invoke(); }
        catch (Exception ex) { _log?.Invoke($"[Overlays] certificaat maken mislukt: {ex.Message}"); return; }
        if (cert == null) return;
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            _tlsListener = l;
            _ = AcceptLoop(l, cert, token);
            _log?.Invoke($"[Overlays] https op poort {port}");
        }
        catch (Exception ex)
        {
            _tlsListener = null;
            _log?.Invoke($"[Overlays] https starten mislukt (poort {port}): {ex.Message}");
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _tlsListener?.Stop(); } catch { }
        _listener = null; _tlsListener = null; _cts = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener l, X509Certificate2? cert, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(token); }
            catch { break; }
            _ = Task.Run(() => Serve(c, cert, token));
        }
    }

    private async Task Serve(TcpClient client, X509Certificate2? cert, CancellationToken token)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(10000);
            Stream stream = client.GetStream();
            if (cert != null)
            {
                var ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert, ClientCertificateRequired = false,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.None   // standaard van het systeem (TLS 1.2/1.3)
                }, cts.Token);
                stream = ssl;
            }
            await using var __ = stream;
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
        catch (Exception ex) when (cert != null && ex is System.Security.Authentication.AuthenticationException or CryptographicException or NotSupportedException or Win32Exception)
        {
            _log?.Invoke($"[Overlays] https-verbinding mislukt: {ex.GetType().Name}: {ex.Message}");
        }
        catch { /* verbinding weg: niet erg */ }
    }

    private static async Task<string?> ReadHead(Stream s, CancellationToken token)
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
        var path = target.Split('?')[0].Split('#')[0].TrimEnd('/').ToLowerInvariant();
        // TikTok LIVE Studio wil een adres met een punt aan het einde: /kerst/alles.html werkt net als /kerst/alles
        if (path.EndsWith(".html")) path = path[..^5];
        if (path == "/index") path = "";
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
