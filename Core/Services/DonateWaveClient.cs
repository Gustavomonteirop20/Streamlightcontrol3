using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// DonateWave Gebeurtenissen-API (https://www.donatewave.nl/developers), polling vanaf deze pc:
///   GET https://www.donatewave.nl/api/v1/events?after=CURSOR   met  Authorization: Bearer dw_evt_…
/// Volgens de documentatie:
///   · sleutel alleen in de Authorization-header, nooit in de URL; geen Origin-header (HttpClient stuurt die niet)
///   · maximaal 60 verzoeken per minuut; poll bij voorkeur elke 2 s (poll_after_seconds)
///   · next_cursor pas bewaren nadat alle ontvangen gebeurtenissen verwerkt zijn
///   · 429 → minstens 60 s wachten; netwerkfout → later opnieuw met dezelfde cursor; 401 → stoppen
///   · alleen tip.received en integration.test verwerken; dubbelen negeren op id; created_at controleren
///   · onbekende velden negeren, tekst is onbetrouwbare invoer
/// </summary>
public sealed class DonateWaveClient : IDisposable
{
    public const string DefaultBaseUrl = "https://www.donatewave.nl";
    /// <summary>Oudere gebeurtenissen (bijv. van toen de app uit stond) geven geen lichtshow meer.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(3);

    private readonly ErrorCenter _errors;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly LinkedList<string> _recentIds = new();
    private readonly HashSet<string> _recentSet = new();

    public DonateWaveClient(ErrorCenter errors, HttpMessageHandler? handler = null)
    {
        _errors = errors;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsHttp = true;
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamLightControl/3");
    }

    /// <summary>Alleen voor tests.</summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    /// <summary>Alleen voor tests: wachttijden verkorten (factor op alle wachttijden).</summary>
    public double DelayScale { get; set; } = 1.0;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusText { get; private set; } = "Uit";
    public DateTime? LastPollAt { get; private set; }
    public DateTime? LastTipAt { get; private set; }
    public int TipsReceived { get; private set; }
    public string Cursor { get; private set; } = "";

    /// <summary>Een tip (of test) om af te spelen. Wordt aangeroepen vóórdat de cursor wordt opgeslagen.</summary>
    public event Action<StreamEvent>? EventReceived;
    /// <summary>De cursor is opgeschoven (alle gebeurtenissen tot hier zijn verwerkt) – bewaren in de instellingen.</summary>
    public event Action<string>? CursorChanged;
    public event Action? StateChanged;

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start(string apiKey, string cursor)
    {
        Stop();
        apiKey = (apiKey ?? "").Trim();
        if (apiKey.Length == 0)
        {
            SetState(ConnectionState.Failed, "Geen sleutel ingevuld");
            _errors.Report("dw.key.missing");
            return;
        }
        _errors.Resolve("dw.key.missing");
        Cursor = cursor ?? "";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetState(ConnectionState.Connecting, "Verbinden met DonateWave…");
        _loop = Task.Run(() => LoopAsync(apiKey, token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        if (State != ConnectionState.Failed) SetState(ConnectionState.Disconnected, "Uit");
    }

    public async Task StopAsync()
    {
        var loop = _loop;
        Stop();
        if (loop != null) { try { await loop.ConfigureAwait(false); } catch { } }
        SetState(ConnectionState.Disconnected, "Uit");
    }

    private async Task LoopAsync(string key, CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            double waitSeconds;
            try
            {
                var (status, body) = await GetAsync(key, token).ConfigureAwait(false);
                LastPollAt = DateTime.Now;
                if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
                {
                    SetState(ConnectionState.Failed, "Sleutel geweigerd – maak een nieuwe koppeling");
                    _errors.Report("dw.unauthorized");
                    return;                                   // documentatie: bij 401 stoppen
                }
                if ((int)status == 429)
                {
                    _errors.Report("dw.ratelimit");
                    SetState(ConnectionState.Reconnecting, "Even wachten (te veel verzoeken)…");
                    waitSeconds = 60;                         // documentatie: minstens 60 s
                }
                else if ((int)status >= 400)
                {
                    failures++;
                    _errors.Report("dw.network", $"HTTP {(int)status}");
                    SetState(ConnectionState.Reconnecting, $"DonateWave gaf fout {(int)status} – opnieuw proberen…");
                    waitSeconds = Backoff(failures);
                }
                else
                {
                    failures = 0;
                    _errors.Resolve("dw.network"); _errors.Resolve("dw.ratelimit"); _errors.Resolve("dw.unauthorized");
                    waitSeconds = Process(body);
                    SetState(ConnectionState.Connected, LastTipAt is { } t ? $"Verbonden · laatste tip {t:HH:mm:ss}" : "Verbonden · wacht op tips");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                failures++;
                _errors.Report("dw.network", ex.Message);
                SetState(ConnectionState.Reconnecting, "Geen verbinding met DonateWave – opnieuw proberen…");
                waitSeconds = Backoff(failures);
            }
            NextPollAt = DateTime.Now.AddSeconds(waitSeconds * DelayScale);
            try { await Task.Delay(TimeSpan.FromSeconds(waitSeconds * DelayScale), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static double Backoff(int failures) => Math.Min(60, new[] { 2, 5, 10, 20, 30, 60 }[Math.Min(failures - 1, 5)]);

    private async Task<(HttpStatusCode, string)> GetAsync(string key, CancellationToken token)
    {
        var after = string.IsNullOrEmpty(Cursor) ? "0" : Uri.EscapeDataString(Cursor);
        var url = $"{BaseUrl.TrimEnd('/')}/api/v1/events?after={after}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var res = await _http.SendAsync(req, token).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var count = 0;
            if (res.IsSuccessStatusCode)
                try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array) count = ev.GetArrayLength(); } catch { }
            Record(new DonateWaveRequest(DateTime.Now, "GET", url, (int)res.StatusCode, res.ReasonPhrase ?? res.StatusCode.ToString(),
                (int)sw.ElapsedMilliseconds, res.IsSuccessStatusCode ? $"{count} gebeurtenis(sen)" : Short(body)));
            return (res.StatusCode, body);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Record(new DonateWaveRequest(DateTime.Now, "GET", url, null, ex.GetType().Name, (int)sw.ElapsedMilliseconds, ex.Message));
            throw;
        }
    }

    // ------------------------------------------------------------------ foutzoeken

    private readonly LinkedList<DonateWaveRequest> _requests = new();
    public const string ApiPath = "/api/v1/events";
    public string ApiUrl => BaseUrl.TrimEnd('/') + ApiPath;
    public DonateWaveRequest? LastRequest { get { lock (_requests) return _requests.Last?.Value; } }
    public int RequestCount { get; private set; }
    /// <summary>Laatste mislukte verzoek (foutcode blijft zichtbaar, ook als het daarna weer lukt).</summary>
    public DonateWaveRequest? LastFailedRequest { get; private set; }
    public DateTime? NextPollAt { get; private set; }
    public IReadOnlyList<DonateWaveRequest> RecentRequests() { lock (_requests) return _requests.Reverse().ToList(); }

    private void Record(DonateWaveRequest r)
    {
        lock (_requests)
        {
            _requests.AddLast(r);
            while (_requests.Count > 30) _requests.RemoveFirst();
        }
        RequestCount++;
        if (r.Status is not (>= 200 and < 300)) LastFailedRequest = r;
        if (r.Status is not (>= 200 and < 300)) _errors.WriteLog($"[DonateWave] {r.Method} {r.Url} → {r.StatusLabel} ({r.Ms} ms) {r.Info}");
    }

    private static string Short(string body)
    {
        var t = new string((body ?? "").Where(ch => !char.IsControl(ch)).Take(160).ToArray()).Trim();
        return t.Length == 0 ? "(leeg antwoord)" : t;
    }

    /// <summary>Verwerkt één antwoord. Geeft de wachttijd tot de volgende poll terug (seconden).</summary>
    internal double Process(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            int got = 0, skipped = 0; var reasons = new List<string>();
            foreach (var ev in events.EnumerateArray())
            {
                got++;
                var e = ToStreamEvent(ev, UtcNow(), out var why);
                if (e == null)
                {
                    skipped++; reasons.Add(why);
                    _errors.WriteLog($"[DonateWave] {Str(ev, "type")} {Str(ev, "id")} overgeslagen: {why}");
                    continue;
                }
                _errors.WriteLog($"[DonateWave] {e.RawType} {e.GiftName} ontvangen{(e.IsTest ? " (test)" : "")}");
                TipsReceived++;
                LastTipAt = DateTime.Now;
                try { EventReceived?.Invoke(e); } catch { }
            }
            if (got > 0)
                LastBatchText = $"{DateTime.Now:HH:mm:ss}: {got} gebeurtenis(sen) ontvangen" +
                    (skipped > 0 ? $", {skipped} overgeslagen ({string.Join(", ", reasons.Distinct())})" : "");
        }
        // Pas nu (alles verwerkt) de cursor opschuiven
        var next = root.TryGetProperty("next_cursor", out var nc) ? nc.ValueKind switch
        {
            JsonValueKind.String => nc.GetString() ?? "",
            JsonValueKind.Number => nc.GetRawText(),
            _ => ""
        } : "";
        if (next.Length > 0 && next != Cursor)
        {
            Cursor = next;
            try { CursorChanged?.Invoke(next); } catch { }
        }
        var poll = root.TryGetProperty("poll_after_seconds", out var pa) && pa.TryGetDouble(out var p) ? p : 2;
        return Math.Clamp(poll, 2, 60);      // nooit vaker dan elke 2 s (max. 60 per minuut)
    }

    /// <summary>Wat er de laatste keer binnenkwam (zichtbaar in de app, handig bij testen).</summary>
    public string LastBatchText { get; private set; } = "";

    /// <summary>Zet één gebeurtenis om. Null = negeren (onbekend type, dubbel of te oud).</summary>
    internal StreamEvent? ToStreamEvent(JsonElement ev, DateTime utcNow, out string why)
    {
        why = "";
        var type = Str(ev, "type");
        if (type is not ("tip.received" or "integration.test")) { why = "type"; return null; }
        var id = Str(ev, "id");
        if (id.Length == 0) { why = "id"; return null; }
        if (!Remember(id)) { why = "dubbel"; return null; }
        DateTime? createdUtc = null;
        if (ev.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String
            && DateTime.TryParse(ca.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var created))
        {
            createdUtc = created;
            if (utcNow - created > MaxAge) { why = $"ouder dan {MaxAge.TotalMinutes:0} min"; return null; }
            if (created - utcNow > TimeSpan.FromSeconds(30)) createdUtc = utcNow;   // pc-klok loopt achter: dan "nu"
        }

        var data = ev.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        long cents = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("amount_cents", out var ac) && ac.TryGetInt64(out var c) ? Math.Max(0, c) : 0;
        var name = Clean(data.ValueKind == JsonValueKind.Object ? Str(data, "display_name") : "");
        var test = type == "integration.test" || (data.ValueKind == JsonValueKind.Object && Bool(data, "test"));
        // "special" is volgens de documentatie true/false; staat er een naam of object in, dan gebruiken we die ook
        var specialName = "";
        var special = false;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("special", out var sp))
        {
            special = sp.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => (sp.GetString() ?? "").Length > 0 && sp.GetString() != "false",
                JsonValueKind.Object => true,
                _ => false
            };
            if (sp.ValueKind == JsonValueKind.String) specialName = sp.GetString() ?? "";
            if (sp.ValueKind == JsonValueKind.Object) specialName = Str(sp, "name");
        }
        // Eventuele extra tekstvelden (bijv. de naam van de shout-out) bewaren om op te kunnen koppelen
        var extra = new List<string>();
        if (specialName.Length > 0) extra.Add(specialName);
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var prop in data.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Name is not ("display_name" or "currency" or "tip_id" or "message"))
                    extra.Add(prop.Value.GetString() ?? "");
        var wheel = data.ValueKind == JsonValueKind.Object && Bool(data, "lucky_wheel");
        if (test && cents == 0) cents = 500;     // test zonder bedrag: doen alsof het € 5 is
        var quantity = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("quantity", out var qv) && qv.TryGetInt32(out var qn) ? Math.Clamp(qn, 1, 50) : 1;
        var label = EventTrigger.Euro(cents) + (wheel ? " · Lucky Wheel" : special ? " · shout-out" : "");
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("quantity", out var ql) && ql.TryGetInt32(out var qln) && qln > 1) label += $" ×{qln}";
        return new StreamEvent
        {
            DedupeKey = "dw:" + id,
            Type = EventTypes.Tip,
            RawType = type,
            User = name,
            DisplayName = test ? "Test · " + (name.Length > 0 ? name : "DonateWave") : name,
            GiftName = label,
            GiftCount = quantity,
            CoinsPerItem = quantity > 1 ? cents / quantity : cents,
            TotalCoins = cents,
            IsTest = test,
            TipKind = wheel ? TipKinds.Wheel : special ? TipKinds.Shoutout : "",
            Comment = Clean(string.Join(" | ", extra.Where(x => x.Length > 0))),
            TipMessage = Clean(data.ValueKind == JsonValueKind.Object ? Str(data, "message") : "", 200),
            OccurredAtUtc = createdUtc,
            FromCloud = true
        };
    }

    private bool Remember(string id)
    {
        lock (_recentIds)
        {
            if (!_recentSet.Add(id)) return false;
            _recentIds.AddLast(id);
            while (_recentIds.Count > 1000) { _recentSet.Remove(_recentIds.First!.Value); _recentIds.RemoveFirst(); }
            return true;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => ""
        } : "";

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Naam is onbetrouwbare invoer: alleen zichtbare tekens, maximaal 40.</summary>
    private static string Clean(string s, int max = 80)
    {
        var chars = s.Where(ch => !char.IsControl(ch)).Take(max).ToArray();
        return new string(chars).Trim();
    }

    private void SetState(ConnectionState s, string text)
    {
        State = s; StatusText = text;
        try { StateChanged?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>Eén verzoek naar DonateWave, voor het overzicht "Verbindingsdetails" (zonder sleutel).</summary>
public sealed record DonateWaveRequest(DateTime Time, string Method, string Url, int? Status, string Reason, int Ms, string Info)
{
    public string StatusLabel => Status is { } s ? $"HTTP {s} {Reason}" : $"geen antwoord ({Reason})";
}
