using System.IO;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// Centrale plek voor alle problemen. Elke fout heeft een vaste sleutel met een begrijpelijke uitleg
/// en oplossing. Dezelfde fout opnieuw verhoogt alleen de teller.
/// </summary>
public sealed class ErrorCenter
{
    private readonly object _gate = new();
    private readonly List<ErrorEntry> _entries = new();
    private readonly string? _logPath;

    public ErrorCenter(string? logDirectory = null)
    {
        if (logDirectory != null) _logPath = Path.Combine(logDirectory, "app.log");
    }

    /// <summary>Nieuwe of bijgewerkte melding (bool = nieuw).</summary>
    public event Action<ErrorEntry, bool>? Reported;
    public event Action<string>? Resolved;
    public event Action? Cleared;

    public IReadOnlyList<ErrorEntry> Snapshot() { lock (_gate) return _entries.ToList(); }

    public record Info(ErrorCategory Category, string Title, string Explanation, string Solution, bool Warning = false);

    public static readonly IReadOnlyDictionary<string, Info> Catalog = new Dictionary<string, Info>
    {
        ["bt.local.notfound"] = new(ErrorCategory.BetterTok, "BetterTok niet gevonden",
            "Er draait geen BetterTok Local WebSocket op poort 21213 t/m 21222. De app kan dus geen TikTok-events ontvangen.",
            "Start de BetterTok-desktopapp op deze pc. Controleer in BetterTok › Instellingen dat Local Mode aanstaat. Kijk in BetterTok welke poort wordt gebruikt en vul die in op de BetterTok-pagina."),
        ["bt.url.invalid"] = new(ErrorCategory.BetterTok, "Ongeldig WebSocket-adres",
            "Het ingevulde adres is geen geldig WebSocket-adres.",
            "Gebruik het standaardadres ws://127.0.0.1:21213 of het adres dat BetterTok zelf toont."),
        ["bt.key.missing"] = new(ErrorCategory.BetterTok, "Geen API-key ingesteld",
            "Cloud Mode heeft een BetterTok API-key nodig.",
            "Maak in BetterTok › Developer › Keys een key aan (begint met btk_live_) en plak die op de BetterTok-pagina. Of gebruik Local Mode, daarvoor is geen key nodig."),
        ["bt.key.format"] = new(ErrorCategory.BetterTok, "API-key heeft een onbekend formaat",
            "BetterTok-keys beginnen met btk_live_. De ingevulde key lijkt niet te kloppen.",
            "Kopieer de key opnieuw uit BetterTok › Developer › Keys, zonder spaties ervoor of erachter."),
        ["bt.unauthorized"] = new(ErrorCategory.BetterTok, "API-key geweigerd",
            "BetterTok meldt UNAUTHORIZED: de key ontbreekt, is ingetrokken of bestaat niet. De app probeert het niet automatisch opnieuw.",
            "Maak een nieuwe key aan in BetterTok en vul hem opnieuw in. Klik daarna op Verbinden."),
        ["bt.tier"] = new(ErrorCategory.BetterTok, "Creator-abonnement nodig voor Cloud Mode",
            "BetterTok meldt TIER_REQUIRED: de key is geldig, maar Cloud Mode vereist een Creator-abonnement (of het abonnement is verlopen).",
            "Gebruik Local Mode (gratis, via de BetterTok-desktopapp) of activeer het Creator-abonnement."),
        ["bt.nousername"] = new(ErrorCategory.BetterTok, "Geen TikTok-account gekoppeld",
            "BetterTok meldt NO_USERNAME: aan je BetterTok-account is nog geen TikTok-account gekoppeld.",
            "Koppel je TikTok-account in BetterTok en verbind opnieuw."),
        ["bt.ratelimit"] = new(ErrorCategory.BetterTok, "Te veel verbindingen of verzoeken",
            "BetterTok meldt RATE_LIMIT of HTTP 429: er zijn te veel verbindingen of verbindingspogingen, of de dienst is tijdelijk vol.",
            "Sluit andere apps/overlays die dezelfde key gebruiken (max. 10 tegelijk). De app probeert het automatisch later opnieuw."),
        ["bt.cloud.network"] = new(ErrorCategory.BetterTok, "Geen verbinding met BetterTok Cloud",
            "De server api.bettertok.app is niet bereikbaar.",
            "Controleer je internetverbinding en firewall. Werkt het internet wel? Probeer dan Local Mode."),
        ["bt.disconnected"] = new(ErrorCategory.BetterTok, "Verbinding met BetterTok verbroken",
            "De WebSocket-verbinding is weggevallen. Events die nu binnenkomen worden gemist.",
            "Automatisch opnieuw verbinden staat aan, de app probeert het zelf. Blijft het mislukken? Herstart BetterTok.", true),
        ["gv.port"] = new(ErrorCategory.Govee, "Poort 4002 is bezet",
            "Een ander programma gebruikt UDP-poort 4002. Daardoor kan de app geen Govee-apparaten zoeken en geen status opvragen. Kleuren sturen werkt wel.",
            "Sluit andere Govee- of LAN-programma's (bijv. Home Assistant-tools, een tweede exemplaar van deze app) en herstart de app."),
        ["gv.notfound"] = new(ErrorCategory.Govee, "Geen Govee-apparaten gevonden",
            "Na het zoeken heeft geen enkele lamp geantwoord.",
            "1) Zet in de Govee Home-app bij je H619E ‘LAN Control’ aan. 2) Zorg dat pc en lamp op hetzelfde netwerk zitten (niet het gastnetwerk). 3) Sta de app toe in Windows Firewall (privé-netwerk). 4) Of voeg het IP-adres handmatig toe (te vinden in je router)."),
        ["gv.noresponse"] = new(ErrorCategory.Govee, "Lamp reageert niet",
            "Een Govee-apparaat gaf geen antwoord op een statusverzoek. Mogelijk is het IP-adres veranderd, staat de lamp zonder stroom of is LAN Control uitgeschakeld.",
            "Klik in Device Manager op ‘Opnieuw verbinden’ om het apparaat opnieuw te zoeken. Controleer of LAN Control aanstaat in Govee Home.", true),
        ["gv.send"] = new(ErrorCategory.Govee, "Commando naar lamp mislukt",
            "Een UDP-commando kon niet worden verzonden.",
            "Controleer je netwerkverbinding en het IP-adres van de lamp in Device Manager."),
        ["gv.nodevice"] = new(ErrorCategory.Govee, "Geen lamp ingesteld",
            "Een lichtshow moest worden afgespeeld, maar er is geen actief Govee-apparaat.",
            "Ga naar Device Manager, zoek je lamp of voeg hem handmatig toe en zet hem op ‘Actief’."),
        ["ev.parse"] = new(ErrorCategory.Event, "Onbekend eventformaat",
            "Er kwam een bericht binnen dat de app niet kon lezen.",
            "Bekijk het ruwe bericht op de BetterTok-pagina (Live events). Als BetterTok het formaat heeft veranderd, stuur dit bericht door zodat de verwerking kan worden aangepast.", true),
        ["ev.noshow"] = new(ErrorCategory.Event, "Koppeling zonder lichtshow",
            "Een event paste bij een koppeling, maar de gekoppelde lichtshow bestaat niet meer.",
            "Open Event Manager en kies opnieuw een lichtshow voor deze koppeling."),
        ["q.full"] = new(ErrorCategory.Queue, "Wachtrij is vol",
            "Er kwamen meer events binnen dan de wachtrij aankan. Effecten met de laagste prioriteit zijn overgeslagen.",
            "Verhoog de maximale wachtrijlengte in Instellingen, verkort je lichtshows of stel cooldowns in voor drukke events zoals likes.", true),
        ["q.play"] = new(ErrorCategory.Queue, "Lichtshow kon niet worden afgespeeld",
            "Tijdens het afspelen ging er iets mis.",
            "Bekijk de details hieronder. Test de show in Light Shows en controleer de lamp in Device Manager."),
        ["sp.notrunning"] = new(ErrorCategory.Spotify, "Spotify draait niet",
            "Spotify-modus staat aan, maar de Spotify-app is niet geopend op deze pc. De app kan dus niet zien welk nummer speelt.",
            "Open de Spotify-desktopapp (niet de webplayer) en start een nummer. Spotify via de browser of telefoon kan de app niet zien.", Warning: true),
        ["sp.audio"] = new(ErrorCategory.Spotify, "Geluid kan niet worden gemeten",
            "De app kan het geluid van je standaard-afspeelapparaat niet uitlezen. De kleuren wisselen nu op een vast tempo in plaats van op de beat.",
            "Controleer in Windows › Geluid dat er een standaard-afspeelapparaat is (speakers of headset). Het werkt ook als Spotify zacht staat, maar niet als het apparaat is uitgeschakeld.", Warning: true),
        ["dw.key.missing"] = new(ErrorCategory.DonateWave, "Geen DonateWave-sleutel ingesteld",
            "De DonateWave-koppeling staat aan, maar er is nog geen sleutel ingevuld.",
            "Maak in DonateWave › Dashboard › Koppelingen een koppeling aan en plak de sleutel (dw_evt_…) bij Verbinding › DonateWave."),
        ["dw.unauthorized"] = new(ErrorCategory.DonateWave, "DonateWave-sleutel geweigerd",
            "DonateWave accepteert de sleutel niet (meer). Sleutels verlopen na 90 dagen of zijn ingetrokken. De app stopt met vragen tot je een nieuwe sleutel invult.",
            "Maak in DonateWave › Koppelingen een nieuwe koppeling, plak de nieuwe sleutel in de app en trek daarna de oude in."),
        ["dw.ratelimit"] = new(ErrorCategory.DonateWave, "Te veel verzoeken naar DonateWave",
            "DonateWave vroeg om even te wachten (maximaal 60 verzoeken per minuut per sleutel). De app wacht 60 seconden en gaat dan verder; er gaan geen tips verloren.",
            "Niets doen. Gebruik je dezelfde sleutel ook in een andere app? Maak dan per app een eigen koppeling.", Warning: true),
        ["dw.network"] = new(ErrorCategory.DonateWave, "Geen verbinding met DonateWave",
            "De app kan donatewave.nl niet bereiken. Hij probeert het steeds opnieuw vanaf de laatst verwerkte tip, dus tips worden alsnog afgespeeld zodra het weer lukt.",
            "Controleer je internetverbinding. Blijft het, kijk dan of donatewave.nl in je browser opent.", Warning: true),
        ["app.save"] = new(ErrorCategory.App, "Instellingen niet opgeslagen",
            "settings.json kon niet worden geschreven.",
            "Controleer of de map %LocalAppData%\\StreamLightControl2 bestaat en niet alleen-lezen is. Heb je een virusscanner die de map blokkeert?"),
        ["app.load"] = new(ErrorCategory.App, "Instellingen konden niet worden geladen",
            "Het instellingenbestand was beschadigd. Er is een back-up van gemaakt en de app is met standaardinstellingen gestart.",
            "Herstel eventueel een eerdere back-up via Instellingen › Back-up terugzetten."),
        ["app.crash"] = new(ErrorCategory.App, "Onverwachte fout",
            "Er ging iets mis in de app. De app draait door.",
            "Staat deze fout er vaker? Kopieer de details en meld het probleem.")
    };

    /// <summary>
    /// Korte, bruikbare omschrijving van een onverwachte fout: soort fout + waar in de app (functie en regel).
    /// De volledige foutmelding gaat naar app.log.
    /// </summary>
    public string Describe(Exception ex)
    {
        var e = ex.GetBaseException();
        var where = new List<string>();
        try
        {
            var st = new System.Diagnostics.StackTrace(e, true);
            foreach (var f in st.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
            {
                var m = f.GetMethod();
                var type = m?.DeclaringType;
                if (type?.Namespace == null || !type.Namespace.StartsWith("StreamLightControl2")) continue;
                var name = (type.DeclaringType ?? type).Name + "." + (m!.Name.StartsWith("<") ? m.Name.Trim('<').Split('>')[0] : m.Name);
                var line = f.GetFileLineNumber();
                where.Add(line > 0 ? $"{name} (regel {line})" : name);
                if (where.Count == 3) break;
            }
        }
        catch { }
        WriteLog("CRASH " + ex);
        return $"{e.GetType().Name}: {e.Message}" + (where.Count > 0 ? "  —  in " + string.Join(" ← ", where) : "");
    }

    public ErrorEntry Report(string key, string? detail = null)
    {
        var info = Catalog.TryGetValue(key, out var i) ? i
            : new Info(ErrorCategory.App, "Fout", "Er is een fout opgetreden.", "Bekijk de details.");
        ErrorEntry entry;
        bool isNew;
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(e => e.Key == key);
            if (existing != null)
            {
                existing.Count++;
                existing.LastSeen = DateTime.Now;
                if (detail != null) existing.Detail = detail;
                entry = existing;
                isNew = false;
            }
            else
            {
                entry = new ErrorEntry
                {
                    Key = key, Category = info.Category, Title = info.Title, Explanation = info.Explanation,
                    Solution = info.Solution, IsWarning = info.Warning, Detail = detail ?? ""
                };
                _entries.Insert(0, entry);
                isNew = true;
            }
        }
        WriteLog($"[{info.Category}] {info.Title}{(detail != null ? " – " + detail : "")}");
        Reported?.Invoke(entry, isNew);
        return entry;
    }

    /// <summary>Verwijdert een melding zodra het probleem vanzelf is opgelost (bijv. weer verbonden).</summary>
    public void Resolve(string key)
    {
        bool removed;
        lock (_gate) removed = _entries.RemoveAll(e => e.Key == key) > 0;
        if (removed) Resolved?.Invoke(key);
    }

    public void ResolvePrefix(string prefix)
    {
        List<string> keys;
        lock (_gate)
        {
            keys = _entries.Where(e => e.Key.StartsWith(prefix)).Select(e => e.Key).ToList();
            _entries.RemoveAll(e => e.Key.StartsWith(prefix));
        }
        foreach (var k in keys) Resolved?.Invoke(k);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Cleared?.Invoke();
    }

    public void WriteLog(string line)
    {
        if (_logPath == null) return;
        try
        {
            lock (_gate) File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { }
    }
}
