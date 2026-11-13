using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting, Failed }

/// <summary>
/// BetterTok Developer API client (Local + Cloud) met automatische reconnect (oplopende wachttijd),
/// heldere foutredenen en ping/pong-controle.
/// </summary>
public sealed class BetterTokClient : IDisposable
{
    public const string CloudUrl = "wss://api.bettertok.app/api/v1/developer/stream";
    public const int FirstLocalPort = 21213, LastLocalPort = 21222;

    private readonly ErrorCenter _errors;
    private CancellationTokenSource? _lifetime;
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TaskCompletionSource<bool>? _pongWaiter;

    public BetterTokClient(ErrorCenter errors) => _errors = errors;

    /// <summary>Alleen voor tests: alternatief Cloud-adres.</summary>
    public string CloudUrlOverride { get; set; } = "";
    public int[] LocalPortsOverride { get; set; } = Array.Empty<int>();

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusText { get; private set; } = "Niet verbonden";
    public string? ConnectedUrl { get; private set; }
    public BetterTokMode? ConnectedMode { get; private set; }
    public string CloudUsername { get; private set; } = "";
    public DateTime? LastMessageAt { get; private set; }
    public int ReconnectAttempt { get; private set; }

    public event Action? StateChanged;
    public event Action<string>? RawMessage;
    public event Action<StreamEvent>? EventReceived;

    private void SetState(ConnectionState state, string text)
    {
        State = state;
        StatusText = text;
        StateChanged?.Invoke();
    }

    /// <summary>Start (of herstart) de verbinding. Keert terug na de eerste poging (true = verbonden).</summary>
    public async Task<bool> ConnectAsync(BetterTokOptions options, string apiKey)
    {
        await DisconnectAsync(silent: true);
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        ReconnectAttempt = 0;
        var first = await TryConnectOnceAsync(options, apiKey, lifetime.Token);
        if (first.socket != null)
        {
            _ = RunAsync(first.socket, options, apiKey, lifetime);
            return true;
        }
        if (first.retry && options.AutoReconnect && !lifetime.IsCancellationRequested)
            _ = ReconnectLoopAsync(options, apiKey, lifetime);
        return false;
    }

    public async Task DisconnectAsync(bool silent = false)
    {
        _lifetime?.Cancel();
        _lifetime = null;
        var socket = _socket;
        _socket = null;
        if (socket != null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(1500);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Afgesloten", cts.Token);
                }
            }
            catch { }
            socket.Dispose();
        }
        ConnectedUrl = null;
        ConnectedMode = null;
        if (!silent) SetState(ConnectionState.Disconnected, "Niet verbonden");
    }

    private async Task RunAsync(ClientWebSocket socket, BetterTokOptions options, string apiKey, CancellationTokenSource lifetime)
    {
        var current = socket;
        while (!lifetime.IsCancellationRequested)
        {
            var (closeCode, reason) = await ReceiveLoopAsync(current, options.Mode, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            _socket = null;
            current.Dispose();

            if (reason == "fatal")
            {
                // UNAUTHORIZED / TIER_REQUIRED / NO_USERNAME: niet automatisch opnieuw proberen (advies BetterTok-docs)
                return;
            }
            _errors.Report("bt.disconnected", closeCode.HasValue ? $"Sluitcode {(int)closeCode}" : reason);
            if (!options.AutoReconnect)
            {
                SetState(ConnectionState.Failed, "Verbinding verbroken");
                return;
            }
            var next = await ReconnectLoopAsync(options, apiKey, lifetime, startLoop: false);
            if (next == null) return;
            current = next;
        }
    }

    private async Task<ClientWebSocket?> ReconnectLoopAsync(BetterTokOptions options, string apiKey, CancellationTokenSource lifetime, bool startLoop = true)
    {
        int[] delays = { 1, 2, 4, 8, 15, 30 };
        while (!lifetime.IsCancellationRequested)
        {
            var delay = delays[Math.Min(ReconnectAttempt, delays.Length - 1)];
            ReconnectAttempt++;
            SetState(ConnectionState.Reconnecting, $"Opnieuw verbinden over {delay} s (poging {ReconnectAttempt})");
            try { await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.Token); }
            catch (OperationCanceledException) { return null; }
            var attempt = await TryConnectOnceAsync(options, apiKey, lifetime.Token);
            if (attempt.socket != null)
            {
                ReconnectAttempt = 0;
                if (startLoop) { _ = RunAsync(attempt.socket, options, apiKey, lifetime); return null; }
                return attempt.socket;
            }
            if (!attempt.retry) return null;
        }
        return null;
    }

    private async Task<(ClientWebSocket? socket, bool retry)> TryConnectOnceAsync(BetterTokOptions options, string apiKey, CancellationToken token)
    {
        SetState(ConnectionState.Connecting, options.Mode == BetterTokMode.Cloud ? "Verbinden met BetterTok Cloud…" : "BetterTok Local zoeken…");
        return options.Mode == BetterTokMode.Cloud
            ? await ConnectCloudAsync(apiKey, token)
            : await ConnectLocalAsync(options, token);
    }

    private async Task<(ClientWebSocket?, bool)> ConnectLocalAsync(BetterTokOptions options, CancellationToken token)
    {
        if (!Uri.TryCreate(options.LocalUrl, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != "ws" && baseUri.Scheme != "wss"))
        {
            _errors.Report("bt.url.invalid", options.LocalUrl);
            SetState(ConnectionState.Failed, "Ongeldig adres");
            return (null, false);
        }
        var ports = new List<int> { baseUri.Port };
        if (options.ScanPorts)
        {
            var range = LocalPortsOverride.Length > 0 ? LocalPortsOverride : Enumerable.Range(FirstLocalPort, LastLocalPort - FirstLocalPort + 1).ToArray();
            ports.AddRange(range.Where(p => p != baseUri.Port));
        }
        // Alle poorten tegelijk proberen: op Windows duurt een geweigerde verbinding ~2 s per poort.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(3000);
        var attempts = ports.Select(async port =>
        {
            var uri = new UriBuilder(baseUri) { Port = port }.Uri;
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try { await ws.ConnectAsync(uri, timeout.Token); return (port, uri, ws: (ClientWebSocket?)ws, error: (string?)null); }
            catch (Exception ex) { ws.Dispose(); return (port, uri, ws: (ClientWebSocket?)null, error: Describe(ex)); }
        }).ToList();
        var results = await Task.WhenAll(attempts);
        // Voorkeur voor de ingestelde poort, daarna de laagste
        var winner = results.Where(r => r.ws != null).OrderBy(r => ports.IndexOf(r.port)).FirstOrDefault();
        foreach (var r in results.Where(r => r.ws != null && !ReferenceEquals(r.ws, winner.ws))) r.ws!.Dispose();
        if (winner.ws != null)
        {
            OnConnected(winner.ws, winner.uri.ToString(), BetterTokMode.Local, $"Verbonden · Local · poort {winner.port}");
            return (winner.ws, true);
        }
        if (token.IsCancellationRequested) return (null, false);
        var lastError = results.Select(r => r.error).LastOrDefault(e => e != null);
        _errors.Report("bt.local.notfound", $"Geprobeerd: poort {string.Join(", ", ports)}. Laatste fout: {lastError}");
        SetState(ConnectionState.Failed, "BetterTok niet gevonden");
        return (null, true);
    }

    private async Task<(ClientWebSocket?, bool)> ConnectCloudAsync(string apiKey, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _errors.Report("bt.key.missing");
            SetState(ConnectionState.Failed, "Geen API-key");
            return (null, false);
        }
        if (!apiKey.StartsWith("btk_live_", StringComparison.Ordinal))
        {
            _errors.Report("bt.key.format");
            SetState(ConnectionState.Failed, "API-key ongeldig");
            return (null, false);
        }
        var url = string.IsNullOrEmpty(CloudUrlOverride) ? CloudUrl : CloudUrlOverride;
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.CollectHttpResponseDetails = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(10000);
            await ws.ConnectAsync(new Uri(url), timeout.Token);
            OnConnected(ws, url, BetterTokMode.Cloud, "Verbonden · Cloud");
            return (ws, true);
        }
        catch (Exception ex)
        {
            var status = ws.HttpStatusCode;
            ws.Dispose();
            if (status == HttpStatusCode.TooManyRequests)
            {
                _errors.Report("bt.ratelimit", "HTTP 429 bij verbinden");
                SetState(ConnectionState.Failed, "Te veel verbindingspogingen");
                return (null, true);
            }
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _errors.Report("bt.unauthorized", $"HTTP {(int)status}");
                SetState(ConnectionState.Failed, "API-key geweigerd");
                return (null, false);
            }
            _errors.Report("bt.cloud.network", Describe(ex));
            SetState(ConnectionState.Failed, "Cloud niet bereikbaar");
            return (null, true);
        }
    }

    private void OnConnected(ClientWebSocket ws, string url, BetterTokMode mode, string text)
    {
        _socket = ws;
        ConnectedUrl = url;
        ConnectedMode = mode;
        LastMessageAt = DateTime.Now;
        _errors.ResolvePrefix("bt.");
        SetState(ConnectionState.Connected, text);
    }

    private async Task<(WebSocketCloseStatus?, string)> ReceiveLoopAsync(ClientWebSocket socket, BetterTokMode mode, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (mode == BetterTokMode.Cloud) _ = PingLoopAsync(socket, pingCts.Token);
        var fatal = false;
        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return (result.CloseStatus, fatal ? "fatal" : result.CloseStatusDescription ?? "Gesloten door BetterTok");
                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > 4 * 1024 * 1024) throw new InvalidDataException("Bericht te groot");
                } while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Text) continue;
                LastMessageAt = DateTime.Now;
                var raw = Encoding.UTF8.GetString(ms.ToArray());
                RawMessage?.Invoke(raw);
                fatal |= HandleMessage(raw);
            }
            return (socket.CloseStatus, fatal ? "fatal" : "Verbinding gesloten");
        }
        catch (OperationCanceledException) { return (null, "Gestopt"); }
        catch (Exception ex) { return (null, fatal ? "fatal" : Describe(ex)); }
        finally { pingCts.Cancel(); }
    }

    /// <summary>Verwerkt één bericht. Geeft true terug bij een fatale fout (niet opnieuw verbinden).</summary>
    private bool HandleMessage(string raw)
    {
        ParsedFrame frame;
        try { frame = BetterTokEventParser.Parse(raw); }
        catch (Exception ex)
        {
            _errors.Report("ev.parse", ex.Message + " · " + (raw.Length > 300 ? raw[..300] + "…" : raw));
            return false;
        }
        switch (frame.Kind)
        {
            case FrameKind.Event when frame.Event != null:
                EventReceived?.Invoke(frame.Event);
                return false;
            case FrameKind.Connected:
                CloudUsername = frame.Username;
                SetState(ConnectionState.Connected, string.IsNullOrEmpty(frame.Username) ? StatusText : $"Verbonden · Cloud · @{frame.Username}");
                return false;
            case FrameKind.Pong:
                _pongWaiter?.TrySetResult(true);
                return false;
            case FrameKind.Error:
                var key = frame.ErrorCode.ToUpperInvariant() switch
                {
                    "UNAUTHORIZED" => "bt.unauthorized",
                    "TIER_REQUIRED" => "bt.tier",
                    "NO_USERNAME" => "bt.nousername",
                    "RATE_LIMIT" => "bt.ratelimit",
                    _ => "bt.disconnected"
                };
                _errors.Report(key, $"{frame.ErrorCode}: {frame.Message}");
                var isFatal = key is "bt.unauthorized" or "bt.tier" or "bt.nousername";
                if (isFatal) SetState(ConnectionState.Failed, key switch
                {
                    "bt.unauthorized" => "API-key geweigerd",
                    "bt.tier" => "Creator-abonnement nodig",
                    _ => "Geen TikTok gekoppeld"
                });
                return isFatal;
            default:
                return false;
        }
    }

    private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), token);
                await SendTextAsync(socket, "{\"type\":\"ping\"}", token);
            }
        }
        catch { }
    }

    private async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(token);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Verbindingstest: Cloud stuurt een ping en wacht op pong; Local controleert of de socket open is.</summary>
    public async Task<(bool ok, string message)> TestAsync()
    {
        var socket = _socket;
        if (socket == null || socket.State != WebSocketState.Open)
            return (false, State == ConnectionState.Failed ? StatusText : "Niet verbonden. Klik op Verbinden.");
        if (ConnectedMode == BetterTokMode.Cloud)
        {
            _pongWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await SendTextAsync(socket, "{\"type\":\"ping\"}", CancellationToken.None);
                var done = await Task.WhenAny(_pongWaiter.Task, Task.Delay(4000));
                return done == _pongWaiter.Task
                    ? (true, $"Cloud antwoordt (pong in {sw.ElapsedMilliseconds} ms).")
                    : (false, "Verbonden, maar geen pong binnen 4 seconden.");
            }
            catch (Exception ex) { return (false, Describe(ex)); }
        }
        var since = LastMessageAt.HasValue ? (DateTime.Now - LastMessageAt.Value).TotalSeconds : double.NaN;
        return (true, double.IsNaN(since)
            ? $"Verbonden met {ConnectedUrl}. Nog geen events ontvangen."
            : $"Verbonden met {ConnectedUrl}. Laatste bericht {since:0} s geleden.");
    }

    public static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return inner switch
        {
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "Verbinding geweigerd (niets actief op deze poort)",
            SocketException { SocketErrorCode: SocketError.HostNotFound } => "Server niet gevonden (geen internet of DNS-probleem)",
            SocketException se => "Netwerkfout: " + se.SocketErrorCode,
            OperationCanceledException => "Time-out",
            _ => inner.Message
        };
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _socket?.Dispose();
        _sendLock.Dispose();
    }
}
