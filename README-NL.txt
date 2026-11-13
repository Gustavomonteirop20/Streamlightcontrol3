STREAM LIGHT CONTROL 3.0
========================
Windows-app (WPF, .NET 8) die TikTok LIVE-events via BetterTok omzet in lichtshows op je Govee-lampen.
De app is zelf de lokale bridge: BetterTok-events komen binnen via WebSocket, lampen worden direct via
Govee LAN Control (UDP) bestuurd. Er is geen cloud-frontend en geen abonnement nodig.


1. INSTALLEREN EN STARTEN
-------------------------
MAKKELIJKST – losse .exe:
  1. Pak deze ZIP uit in een nieuwe map.
  2. Dubbelklik op MaakExe.bat (de eerste keer 1–3 minuten, internet nodig).
  3. Klaar: op je bureaublad staat de map "Stream Light Control" met StreamLightControl.exe
     (plus bestanden die de app nodig heeft – laat die staan) en een snelkoppeling
     "Stream Light Control". Vanaf nu start je de app gewoon daarmee.
  Nodig: de .NET 8 SDK (staat er al als je Visual Studio hebt). Het .exe-bestand zelf heeft
  daarna niets meer nodig. Naar een andere pc? Kopieer dan de hele map.
  Na een nieuwe versie: gewoon opnieuw MaakExe.bat dubbelklikken.

Via Visual Studio (voor aanpassen/debuggen):
  Dubbelklik op StreamLightControl2.csproj → Build → F5.

Vraagt Windows Firewall om toegang? Kies "Privénetwerken" en klik op Toestaan.
Zonder die toestemming kan de app je lampen niet vinden.

Je instellingen uit versie 2.0 (%LocalAppData%\StreamLightControl2\settings.json) worden bij de eerste
start automatisch omgezet: je oude eventregels worden lichtshows + koppelingen, je lamp-IP en
BetterTok-adres blijven staan.


2. EERSTE KEER INSTELLEN (5 MINUTEN)
------------------------------------
  1. Govee Home-app → je H619E → instellingen → zet "LAN Control" aan.
  2. Start BetterTok op deze pc (Local Mode staat standaard aan).
  3. Open Stream Light Control → Device Manager → "Apparaten zoeken" → "Toevoegen".
     Klik op "Testkleur sturen": je strip moet 2× paars knipperen.
  4. Kijk links onderin: BetterTok en Govee moeten groen zijn.
  5. Event Manager → "Event simuleren" → Follow. Je lamp moet 2× blauw knipperen.
  6. Dashboard → "Start automatisering". Bovenin staat nu LIVE.


3. WAT ER IN DE APP ZIT
-----------------------
Zijbalk: Dashboard · Light Shows · Event Manager · Gift Manager · Follow Manager · Device Manager ·
Event History · Instellingen, plus Wachtrij, BetterTok en Error Center. Onderin: status van BetterTok
en Govee, versie en een instellingenknop.

  Dashboard        Welkom, algemene status, Start/Stop, LIVE-indicator, statistieken (zelf te kiezen),
                   laatste events, huidig effect, wachtrij, apparaten, snelle acties.
  Light Shows      Editor met kleurkiezer (vlak + HEX + RGB), helderheid, duur, flitsen, overgang,
                   herhalingen, visuele tijdlijn, preview. "Afspelen op lampen" stuurt de show echt naar
                   de lampen (ook niet-opgeslagen wijzigingen). Opslaan, dupliceren, hernoemen,
                   verwijderen, presets herstellen.
  Event Manager    Eén overzicht: eventtype → lichtshow, prioriteit, aan/uit, test. Details: cooldown,
                   max. in wachtrij, onderbreken, voorwaarden. Event simuleren door de echte verwerking.
  Gift Manager     Regelbouwer voor coin-bereiken (volledig aanpasbaar, met waarschuwing bij overlap of
                   gaten), specifieke gifts (meerdere namen → één show), herkende gifts uit je history,
                   tester "welke regel wint?", combo-instellingen.
  Follow Manager   Follow-effect, één keer per kijker, follows bundelen, recente followers.
  Device Manager   Zoeken, handmatig toevoegen, verbinding testen (devStatus: antwoord = LAN Control
                   actief), aan/uit, helderheid, kleur, status, hernoemen, opnieuw verbinden (vindt een
                   nieuw IP-adres), testkleur.
  BetterTok        Local / Cloud, WebSocket-status, automatisch opnieuw verbinden, API-key versleuteld
                   (Windows DPAPI), ruwe berichten, verbinding testen, uitleg waarom het mislukt.
  Event History    Tijdstip, gebruiker, event, gift, coins, lichtshow, status. Filters, zoeken, CSV-export.
  Wachtrij         Huidig effect, volgende effecten, prioriteit (aanpasbaar), gebruiker, event, wachttijd.
                   Overslaan, stoppen/hervatten, verwijderen, omhoog/omlaag, leegmaken.
  Quick Actions    Aan, uit, Rainbow, rood, groen, blauw, wit, stop alles.
  Presets          Rainbow, Police, Fire, Ice, Party, Victory, Follow, Big Gift (+ Like, Share en
                   gift-niveaus). Allemaal aan te passen en terug te zetten.
  Safe Mode        Eén klik: niets wordt meer afgespeeld, instellingen blijven bewaard (ook na herstart).
  Stream Mode      Compact venster (optioneel altijd bovenop) met alleen de belangrijkste knoppen.
  Error Center     Elke fout met uitleg in gewone taal + oplossing. Opgeloste problemen verdwijnen vanzelf.

Hoe een event wordt verwerkt:
  BetterTok-bericht → omzetten naar vast formaat (Local én Cloud) → dubbel? → combo nog bezig? →
  precies één koppeling kiezen (specifieke giftnaam > hoogste prioriteit > smalste coin-bereik) →
  cooldown / limiet → wachtrij (hoogste prioriteit eerst) → light show engine → Govee LAN (UDP 4003) →
  daarna de lamp terug naar hoe hij was.

Opslag (alles in %LocalAppData%\StreamLightControl2):
  settings.json   alle instellingen, shows, koppelingen, apparaten (automatisch, atomisch opgeslagen)
  history.json    Event History (laatste 3000)
  events.log      ruwe BetterTok-berichten (zoals in 2.0)
  app.log         foutmeldingen


3b. SONG-LICHTSHOWS (DONO-SOUNDS)
---------------------------------
In Light Shows staan 32 shows met een ♪ en het label "Song", één per dono-sound:
Krachwhore, Kickpong, Turn on the microwave, Mystery bag welcome, Move it, Sweet zaagoline,
Just the way you are, Surging Sparks, Numb (epic), Numb, Runaway, Noah – Shivers,
Somebody that I used to know, Mirror, Never Coming Down,
en nieuw in 3.10: Party Rock Remix, So good to be bad, One kiss (Cyber Gunz), Pyrotechnik (Noiseflow),
Hoe detonate ik zo een C4, Incoming (Adjuzt), F1 hardstyle remix, Titanium (Rebelion), Fireflies remix,
F#ck the haters (Multilator), Hit the gas, Can can, So Incredible, El sonidito,
Dual Damage – Everywhere I go, 1 team (Outsiders), Feelin' high (Krowdexx),
en nieuw in 3.13 (je DonateWave-geluidsbibliotheek): All I want for Christmas (Lunos), Banger after banger, B.O.A.,
Brennan Heart – Imaginary, Burning Skies (RXZO x Maximize), Helpless, Lekkerfaces & Noxiouz – Move It,
Paradise (Noiseflow), Pump it, TikTok-sound (SaveTik), City Lights (Rebelion), Oceans (Mutilator), Twilight Zone,
Deadly Guns uptempo (Wezzel), DonateWave-sound 27-08.
Song-shows waarvan de sound niet meer in je map zit, haalt de update weg (met de koppeling ernaartoe;
een eigen koppeling blijft staan, zonder show).

De 17 nieuwe shows zijn op dezelfde manier gemaakt als de festival-versies hieronder: tempo en eerste
kick gemeten (130 tot 205 BPM), drops en breaks automatisch uit de energie en de kicks, en waar een
TikTok-edit een stuk van de sound verschuift, schuift het licht in die drop mee (tot ±75 ms).

Gemaakt uit een analyse van de echte audio (tempo, beats, kicks, energie, drops). Festivalstijl in scènes:
  - in harde stukken: elke 4e wissel een zwart stukje en elke 8e wissel een witte flashbang
  - nooit sneller dan ±550 ms per kleurwissel (een Govee-strip pakt anders niet elke kleur);
    bij 160 BPM is dat elke 2 beats (±750 ms), dus de wissel valt nog steeds op de klap
  - per 2 maten een "scène" met een kleurenpaar dat mooi samengaat (magenta + cyaan, oranje + blauw,
    paars + geel, ...), daarna een nieuw paar. Zo komen alle kleuren in een song langs.
  - opeenvolgende kleuren verschillen altijd sterk (nooit blauw → lichtblauw)
  - niet elke drop iets eigens: alleen de grote drop krijgt een witte klap
  - opbouw naar de drop: kleur en wit om en om, steeds feller
  - rustige stukken: zachte overgangen, langzaam
  - de hele strip één kleur tegelijk (geen helft/helft)

FESTIVAL-VERSIES (versie 3.8) – opnieuw gemaakt uit de sound zelf:
  · drops precies op de beat waar de kick met volle energie terugkomt (gemeten, niet afgerond)
  · overal felle kleur, nergens zwart: intro/break ≥ 60 %, groove 75–90 %, drop 100 %, outro 100 → 60 %
  · per song 3 neonkleuren (kleurenpaar + accent), elke 2 maten een nieuw paar
  · witte flash alleen op beats waar in de sound echt een kick zit; kleurwissel op elke beat
  · build-up vóór elke drop: per beat → ½ → ¼ beat, eindigend in strobe wit ↔ kleur
  · elke 4e maat van een drop: strobe wit ↔ kleur op ¼ beats (nooit langer dan 2 maten)
  · laatste drop (of 2e helft van een enkele drop): ½-beat-wissels, strip A/B om-en-om, extra strobes
  · Instellingen › Verlichting › "Helderheid lichtshows" (standaard 60 %): alle shows en Spotify
    naar verhouding minder fel, voor je streamcamera. Te fel/overbelicht? Lager zetten.
  · na de show: herstel naar de vorige kleur. Instellingen › "Strobes beperken" = max. 3 witte flitsen/s.

OP DE ECHTE AANSLAGEN (versie 3.11):
  · in de drops zit het licht niet meer op een strak raster maar op wat er echt in de sound gebeurt:
    wit exact op elke kick (gemeten tot op ±3 ms), en een kleurwissel op elke zaag-/lead-aanslag ertussen
  · de drop begint exact op de eerste kick van de drop
  · strobes en build-ups schuiven als geheel mee naar de kicks; overige wissels naar de dichtstbijzijnde klap
  · gemeten over alle 32 sounds: kleurwissels op een echte aanslag van 33 % naar 53 %, witte flitsen op
    een kick van 58 % naar 66 % (de rest zijn bewust strobes tussen de kicks)
  · valt het licht net ná de klap? Instellingen › "Vertraging meten" of "Licht eerder sturen".

BEAT-TRACKING (versie 3.15):
  · de app volgt per beat de echte kicks in de sound in plaats van één vast tempo. TikTok-edits waar een stuk
    verschoven is (bijv. Titanium: vanaf 39,5 s 58 ms eerder) en kleine tempo-afwijkingen schuiven nu mee
  · kick = lage klap die in een regelmatige reeks staat; losse bas- en zaagnoten tellen niet als kick
  · elke drop begint op de echte kick die er het dichtst bij ligt; de strobe ervoor schuift als geheel mee
  · gemeten over alle 47 sounds: 92 % van de kicks in de drops heeft een lichtwissel binnen 5 ms,
    179 van de 183 drops beginnen binnen 20 ms van de klap (Titanium: 95 % en alle drops op 0 ms)
  · nog niet perfect: El sonidito (1 drop, geen kick in de buurt), One kiss (2 drops 41–63 ms),
    Sweet zaagoline (1 drop 64 ms). In stukken zonder vaste kick (intro, breaks) volgt het licht het tempo.

TWEE VERSIES PER SHOW (de app kiest zelf):
  CLUB    – voor strips die "direct springen" (Device Manager › Test: direct springen = Ja).
            Elke beat een wissel (±2,5 per seconde), strobe in de opbouw (wit/zwart), blackout op de
            laatste beat vóór de drop, flashbang óp de drop, zwart stukje + flashbang in elke frase,
            elke 4e frase een strobe-maat. Langzame nummers (< 110 BPM) krijgen ook een off-beat.
  VEILIG  – voor strips met fade: nooit sneller dan ±550 ms.
  Snelheidslimiet 400 ms of sneller = club-versie, anders veilig. Shows die je zelf hebt aangepast
  worden nooit overschreven. Police, Party, Follow en de gift-presets hebben ook een club-versie.

LAMPEN TE TRAAG? (Instellingen → Verlichting en Device Manager)
  1. Device Manager → Snelheidstest: je strip knippert steeds sneller (2 t/m 8 per seconde).
     Vul in tot waar hij het netjes bijhield (standaard 550 ms). De app slaat daarna wissels over die te snel komen,
     zodat de klappen op de beat overblijven. Dit geldt voor álle shows.
  2. Komt het licht net ná de beat? Instellingen → "Licht eerder sturen" op 100–150 ms.
  3. Govee Home-app: zet muziekmodus/DreamView uit en sluit de app tijdens je stream.
     Een sterk 2,4 GHz-wifisignaal bij de strip helpt ook (router dichterbij of een repeater).

Al gekoppeld, gelijk aan je BetterTok Actions (een gift speelt de hoogste drempel die hij haalt):
     1–89 coins   Kleine gift                  1500–1999   ♪ Pyrotechnik (Noiseflow)
    90–99         ♪ Just the way you are       2000–3998   ♪ Sweet zaagoline
   100–198        ♪ Kickpong                   3999–4999   ♪ Noah – Shivers
   199            Middelgrote gift (199 muziek) 5000–5999  ♪ Dual Damage – Everywhere I go
   200–298        ♪ Incoming (Adjuzt)          6000–9999   ♪ Mirror
   299–498        ♪ Hit the gas                10000–14999 ♪ Fireflies remix
   499–599        ♪ Surging Sparks             15000–29999 ♪ Runaway (Aura Dono sound)
   600–998        ♪ Krachwhore                 30000+      ♪ Titanium (Rebelion)
   999–1499       ♪ Numb
Gifts met een eigen sound (gaan altijd voor het coin-bereik):
   Diamantboom → Never Coming Down     Vuurwerk → Party Rock Remix      Sportwagen → So good to be bad
   Hartjes → One kiss                  Sterke finish / Racedebuut → F1 hardstyle remix
   Super GG → F#ck the haters          Misha-beer → Can can             Sterrenstelsel → So Incredible
   Hoed en snor → El sonidito          Side by Side → 1 team (Outsiders) Harthanden → Feelin' high
Staan niet in je BetterTok-actions (show bestaat wel, niet gekoppeld): Turn on the microwave,
Mystery bag welcome, Move it, Numb (epic), Somebody that I used to know, Hoe detonate ik zo een C4.
Koppelen kan in Regels / Triggers (kies de ♪-show).

Police / snelle effecten: knipperen gebeurt nu standaard via "kleur naar zwart" (1 commando per flits).
De oude manier (lamp uit/aan) was te traag voor de strip en flitsen vielen weg. Bestaande installaties
worden automatisch omgezet; Police en Party zijn vernieuwd.


3c. AUTOMATISCHE UPDATES (VOOR IEDEREEN DIE DE APP GEBRUIKT)
-------------------------------------------------------------
Hoe het werkt:
  - De app kijkt bij het opstarten en elke 6 uur op GitHub of er een nieuwe versie is.
  - Bij het opstarten: automatisch downloaden → controlecode (SHA-256) checken → installeren → herstarten.
  - Tijdens gebruik / streamen: alleen een melding "Nieuwe versie beschikbaar" met "Nu bijwerken".
  - Instellingen, lichtshows en koppelingen blijven altijd bewaard (die staan in %LocalAppData%).
  - Werkt in de .exe-versie (niet als je vanuit Visual Studio start).
  - Instellingen → Updates: zelf controleren, automatisch aan/uit, update-adres.

EENMALIG INSTELLEN (± 15 minuten):
  1. Maak een gratis account op github.com.
  2. Maak een nieuwe OPENBARE repository, bijvoorbeeld "StreamLightControl".
  3. Installeer Git (git-scm.com) en zet deze projectmap erin:
       git init
       git add -A
       git commit -m "Eerste versie"
       git branch -M main
       git remote add origin https://github.com/JOUWNAAM/StreamLightControl.git
       git push -u origin main
  4. Vul in de app bij Instellingen → Updates het adres in:  JOUWNAAM/StreamLightControl
     (of laat het in de code vastzetten in Core/Services/UpdateService.cs → DefaultRepo,
      dan hoeven andere gebruikers niets in te vullen).

NIEUWE VERSIE UITBRENGEN:
  Dubbelklik op NieuweVersie.bat → vul een hoger versienummer in (bijv. 3.2.0) en wat er veranderd is.
  GitHub bouwt de app (± 5 minuten, zie tabblad "Actions") en publiceert hem.
  Iedereen krijgt de update bij de volgende keer opstarten.
  Het versienummer moet altijd hoger zijn dan het vorige.

DELEN MET ANDEREN:
  Stuur ze de link naar je GitHub-releases (github.com/JOUWNAAM/StreamLightControl/releases).
  Ze downloaden het zip-bestand, pakken het uit en starten StreamLightControl.exe.
  Daarna werkt alles automatisch bij.


TIJDLIJN (Light Shows): blokken zo breed als ze duren, met tijdschaal in seconden.
  Klik op een blok = die stap bewerken (de stappenlijst springt mee). Muis erboven = details.
  Zoomen: + / − / Passend, of Ctrl + scrollwiel. Shift + scrollwiel = opzij scrollen.
  Tijdens afspelen loopt een rode afspeelkop vloeiend mee en blijft in beeld.

3c2. LICHTSHOW-AFBEELDINGEN IMPORTEREN EN DELEN
----------------------------------------------
Een lichtshow kan in een gewone PNG-afbeelding zitten: je ziet de kleuren van de show, en de show
zelf zit onzichtbaar in het bestand.
  Importeren:  Light Shows › Importeren (of sleep de afbeelding op de Light Shows-pagina).
               Bestaat er al een show voor dat nummer, dan kun je hem vervangen (gift-koppeling blijft).
               Nieuwe show? Dan vraagt de app bij hoeveel coins hij moet spelen (zelfde aantal als de
               dono-sound in BetterTok) – of koppel hem later in Gift Manager.
  Exporteren:  Light Shows › kies een show › Exporteren → .png (om te delen) of .json.
Nieuwe dono-sound? Stuur de mp3 naar Claude, dan maakt Claude er een lichtshow-afbeelding van die je
zo kunt importeren. In Lichtshow-afbeeldingen.zip zitten de afbeeldingen van alle 32 songs.


3d. SPOTIFY-MODUS (LAMPEN OP DE MUZIEK, ZONDER STREAM)
-----------------------------------------------------
Bovenaan in de zijbalk kies je de MODUS:
  Stream   = BetterTok + Govee. TikTok-events sturen je lampen (zoals altijd).
  Spotify  = Spotify + Govee. Het nummer dat speelt stuurt je lampen. BetterTok staat dan helemaal uit.
Er staat altijd maar één modus aan, zodat ze elkaar nooit storen. De app onthoudt je keuze na herstart.

Zo werkt Spotify-modus:
  - Open de Spotify-DESKTOPAPP op deze pc en speel iets af. Inloggen of koppelen is niet nodig: de app
    leest het nummer uit het Spotify-venster (werkt ook als Spotify geminimaliseerd is).
  - Elk nummer krijgt zijn eigen 4 felle kleuren (zelfde nummer = altijd dezelfde kleuren).
    Elke wissel is een duidelijk andere kleur (nooit blauw → lichtblauw).
  - Het geluid van je speakers/headset wordt gemeten (Windows-volumemeter) om de beat te vinden.
  - Nooit sneller dan je snelheidslimiet (standaard 550 ms).
  - Alle 8 kleuren komen langs; elk nummer heeft zijn eigen volgorde en beginkleur.
  - Tempo-voorspelling: na een paar seconden kent de app het BPM en stuurt hij de kleur al vóór
    de beat (zoveel ms als "Licht eerder sturen"), zodat het licht precies op de klap komt.
Drie stijlen (pagina Spotify):
  Op de beat  – kleurwissel op de klap; 4 wissels tussen twee kleuren, dan het volgende paar.
                In harde stukken zwarte stukjes en flashbangs, op de drop wit-zwart-wit (uit te zetten)
  Rustig      – de kleuren lopen zacht in elkaar over (instelbaar 2–20 s per kleur)
  Eén kleur   – één kleur per nummer die op de beat feller en zachter wordt
Rave / uptempo / zaag herkennen (standaard Automatisch): hoort de app 128 BPM of sneller met een vaste
kick, dan wordt het festival-licht zoals bij de dono-sounds: witte flash op elke kick, kleur op de
¼ beat, strobe elke 4e maat, strobe-klap bij de drop, na 8 maten strip A/B om-en-om; break = rustiger.
Op de Spotify-pagina zie je "RAVE / ZAAG HERKEND". Kan ook op Altijd of Uit.
Pauze in Spotify → strip brandt zacht (uit te zetten). Safe Mode werkt ook in Spotify-modus.
Let op: alle geluid van je pc telt mee voor de beat (ook een video of game). Spotify in de browser
of op je telefoon kan de app niet zien.


3e. VERTRAGING TUSSEN PC EN LEDSTRIP WEGWERKEN
---------------------------------------------
1. Meten (1 minuut): Instellingen › Verlichting › "Vertraging meten". Je strip flitst 12 keer wit;
   druk op de SPATIEBALK precies als je de flits op de strip ZIET. De app rekent uit hoeveel je strip
   achterloopt en zet "Licht eerder sturen" daar automatisch op. Geldt voor lichtshows én Spotify.
   Doe het 2× en kijk of je ongeveer hetzelfde krijgt.
2. Netwerk (scheelt het meest):
   - Geef de strip een vast IP-adres in je router (DHCP-reservering), dan hoeft niets opnieuw te zoeken.
   - Strip op 2,4 GHz met een sterk signaal: router dichterbij of een wifi-repeater bij de strip.
   - Pc liefst met een kabel aan de router.
   - Sluit de Govee Home-app op je telefoon en zet muziekmodus/DreamView uit: die stuurt anders tussendoor.
3. Geluid:
   - Bluetooth-koptelefoon of -speaker loopt zelf 150–250 ms achter. Dan lijkt het licht te vroeg of te
     laat. Gebruik bij voorkeur een bedrade headset of speakers, of meet de vertraging opnieuw.
4. Kleuren direct laten springen (geen fade): Device Manager › "Test: direct springen".
   Normaal laat een Govee-strip elke kleur zelf zacht overlopen (±0,5–1 s). Daardoor lijkt het licht
   achter te lopen en mist hij snelle wissels. De app kan het realtime-protocol van Govee gebruiken
   (hetzelfde als Govee Desktop-schermsync, Razer Chroma en OpenRGB): dan springt de kleur meteen.
   De test laat eerst DIRECT en daarna GEWOON zien; zag je bij direct de kleur hard omspringen, kies
   "Ja, direct aanzetten". Doe daarna opnieuw de Snelheidstest: vaak kan de strip nu sneller.
   Vaste kleuren en het herstellen na een show gaan altijd via het gewone commando (die blijven staan).
5. App: "Pauze tussen commando's" kan omlaag naar 40–60 ms als je strip het bijhoudt (Snelheidstest).


3f. DONATEWAVE-TIPS (VERSIE 3.12)
Elke tip via DonateWave speelt een lichtshow, naast je TikTok-gifts.
1. DonateWave › Dashboard › Koppelingen › "Koppeling toevoegen". Kopieer de sleutel (dw_evt_…).
2. App › Verbinding (BetterTok-pagina) › kaart "DonateWave": plak de sleutel › "Opslaan en verbinden".
   De stip wordt groen: "Verbonden · wacht op tips".
3. Testen: DonateWave › Koppelingen › Test (of Livebediening › Testtip versturen). De tip staat dan in
   Geschiedenis en de lamp speelt de show.
Standaard (zelfde bedragen als je Alert-effecten in DonateWave):
   € 0,01 – € 9,99   ♪ TikTok-sound (SaveTik)          (DonateWave speelt SaveTik.io_7706214937663769889)
   € 10  – € 29,99   ♪ B.O.A.                          (onderbreekt een lopende show)
   € 30 en hoger     ♪ All I want for Christmas (Lunos) (onderbreekt een lopende show)
   Shout-out (elk bedrag) ▸ Party (onderbreekt een lopende show)
   Een shout-out-regel gaat voor de bedrag-regels; staat hij uit, dan telt alleen het bedrag. Bij Regels kun je
   per tip-regel kiezen: "Elke tip", "Alleen shout-outs" of "Alleen Lucky Wheel".
SPECIALE SHOUT-OUTS (versie 3.16): elke special heeft zijn eigen regel met de "extra sound na de animatie"
als show, en de show begint pas als de animatie klaar is (duur gemeten uit de animatievideo):
   Desert King € 500 (10,6 s) ▸ Pump it          Royal Voyage € 20 (6,4 s) ▸ Imaginary
   Golden Rush € 50 (7,1 s) ▸ Banger after banger Starlight Kingdom € 10 (6,1 s) ▸ DonateWave-sound 27-08
   Spiky Groove € 5 (7,5 s) ▸ Helpless            Enchanted Hat € 8 (7,3 s) ▸ Burning Skies
   Inferno Heir € 100 (8,0 s) ▸ Move It (Lekkerfaces)  Lion's Crown € 80 (7,8 s) ▸ Paradise
   Acorn Fortune € 10 (6,6 s) ▸ Oceans            Cosmic Launch € 50 (7,4 s) ▸ City Lights
LET OP: DonateWave stuurt alleen het bedrag mee, niet welke special. Starlight Kingdom en Acorn Fortune
kosten allebei € 10, Golden Rush en Cosmic Launch allebei € 50 – dan wint de eerste. Geef ze in DonateWave
een eigen prijs (bijv. Acorn Fortune € 11) en zet die prijs ook in de regel, dan klopt het altijd.
Sound en show lopen gelijk: de app rekent vanaf het moment van de tip (DonateWave created_at), dus de
2 seconden polling worden ingehaald (de show begint dan iets verder in).
Versie 3.18: de overlay laat een tip iets later zien dan het moment van de tip (ontvangen + animatie en
geluid laden). Daarom start het licht standaard 1000 ms later ("Licht later laten beginnen dan de tip" op de
DonateWave-kaart, 0–5000 ms). Loopt het licht nog vóór op de sound: hoger zetten; loopt het achter: lager.
ZONE-EFFECTEN (versie 3.32) – Device Manager › "Zones op stream".
De stukjes strip die op stream te zien zijn (bij jou segmenten 9 t/m 16, al ingevuld) krijgen tijdens shows
festival-effecten zoals in een hardstyle-tent: beams om-en-om, een scanner die over het stuk loopt, licht vanuit het
midden en contrasterende washes in magenta, rood en paars. De rest van de strip houdt de kleur van de show; op de
kicks gaat de hele strip wit. Gezellige shows (Thomas) krijgen een zacht kleurverloop. Kies bij "Methode die werkte"
A (Direct-zones) of B (Segmenten), wat bij de zones-test werkte. Uitzetten: schakelaar "Festival-effecten op deze zones".

ZONES-TEST (versie 3.31) – Device Manager › kies je strip › "Zones op stream (test)".
Zoekt uit welke stukjes van de strip op stream te zien zijn, als voorbereiding op shows waarbij alleen die stukjes
(of elk stukje apart) meedoen. Methode A gebruikt de Direct-modus (snel, ook voor shows op de beat);
methode B de onofficiële segment-opdracht (ptReal) als reserve. Zone voor zone licht geel op en het nummer staat
in beeld. Vul in welke nummers je op stream zag (bijv. 1-3, 18-20) en controleer met "Laat alleen deze zones oplichten".

BINNENKOMST-SHOWS (versie 3.30) – Regels › "♪ Binnenkomst Thomas" en "♪ Binnenkomst Diego".
Komt Thomas of Diego de LIVE binnen (BetterTok: join/member), dan speelt zijn eigen ♪-show op zijn binnenkomst-sound
(Thomas: kinderliedje, gezellige show met warme kleuren en zachte overgangen, 28,4 s; Diego "Black Water" 160 BPM festival, 32,0 s). De volgende show wacht tot de sound klaar is.
Andere kijkers krijgen deze show niet. Klopt de naam niet precies? Pas bij de regel "Alleen voor deze kijker(s)"
de TikTok-naam (zonder @) of weergavenaam aan; meerdere namen met een komma. Testen: Regels › "Thomas komt binnen".

KERST-OVERLAYS (versie 3.27) – BetterTok › kaart "Kerst-overlays".
De app biedt op deze pc eigen kerstversies van de DonateWave-onderdelen aan, live bijgewerkt met elke tip
die via je DonateWave-sleutel binnenkomt:
  http://127.0.0.1:7531/kerst/alert.html       Tipalert (600 × 420) – verschijnt tegelijk met die van DonateWave
  http://127.0.0.1:7531/kerst/doel.html        Tipdoel als zuurstok (520 × 200)
  http://127.0.0.1:7531/kerst/ranking.html     Lijst van de Kerstman, top 5 van de maand (420 × 480)
  http://127.0.0.1:7531/kerst/recent.html      Onder de boom, laatste 5 tips (420 × 420)
  http://127.0.0.1:7531/kerst/supporters.html  Laatste supporter, kerstkampioen van de week, vandaag (420 × 420)
  http://127.0.0.1:7531/kerst/alles.html       Alles onder elkaar, past zich aan 432 × 768 aan
Zet het adres als browserbron in OBS of als linkbron in TikTok LIVE Studio (op dezelfde pc).
TikTok LIVE Studio accepteert alleen een echt webadres (https + .nl). Klik daarvoor één keer op
"Instellen voor TikTok LIVE Studio" (versie 3.29): de app maakt een eigen certificaat en zet in het hosts-bestand
"127.0.0.1 kerst.slc-overlay.nl", zodat dit adres alleen op deze pc naar de app wijst. Windows vraagt twee keer
om toestemming. Daarna gebruik je "Kopieer voor TikTok", bijv. https://kerst.slc-overlay.nl:7532/kerst/alles.html.
Voor OBS werkt ook het gewone adres (Kopieer voor OBS).
Testtips staan altijd in "Onder de boom" en bij "Laatste supporter"; voor doel, ranking en vandaag zet je "Testtips meetellen" aan.
Extra's achter het adres: ?scale=1.3 (groter/kleiner), ?still=1 (zonder beweging), ?bg=1 (donkere achtergrond, voorbeeld).
Zet hetzelfde onderdeel in DonateWave › Overlaybouwer uit, anders staat het er dubbel.
Doel, testtips meetellen, alert-duur en poort stel je in op de kaart. Tips worden bewaard in donations.json.
Let op: tips die binnenkwamen terwijl de app uit stond, ziet de app niet (de ranking telt vanaf nu).

SOUND-DUUR PER REGEL (versie 3.25) – Regels › kies een regel › "Sound bij dit event duurt (seconden)".
Speelt BetterTok bij een event een sound (bijv. bij een follow), vul dan hier in hoe lang die sound duurt.
Volgen er 2 mensen vlak na elkaar, dan knipperen de lampen voor de tweede pas als de sound van de eerste
helemaal klaar is. 0 = alleen de lichtshow (zoals vroeger).

GEMETEN OP JE EIGEN OVERLAY (versie 3.24) – 7 tests op 12-11-2026, tijden t.o.v. het moment van de tip in de API.
Deze tijden gaan vóór alles hieronder (3.18–3.23):
  · gewone tip: de sound begint 0,14 s vóór het API-tijdstip (de app haalt de polling in)
  · shout-out: animatie in beeld na 0,79 s · extra sound 0,80 s na het einde van de animatie
  · na de sound: voorleesstem tot de alert klaar is 3,24 s ("Na de sound: voorleesstem…", standaard 3240)
  · daarna: volgende animatie na 2,44 s, volgende sound na 1,5 s
  · VOLGORDE bij meerdere shout-outs vlak na elkaar (gemeten met 2 en met 3):
      animatie 1 → animatie 2 + sound 2 → animatie 3 + sound 3 → … → als laatste sound 1
    (animatie → volgende animatie 1,74 s: "Bij 2+ shout-outs tegelijk…", standaard 1735)
    De app speelt de shows in precies die volgorde; de wachtrij van de app wacht niet meer op show 1.
  · shout-outs gaan vóór wachtende gewone tips; een gewone tip tijdens een animatie duwt niets opzij
  · "Correctie: licht later (+) of eerder (−)" staat standaard op 0 (= precies zoals gemeten)
  · let op: de allereerste keer dat een sound in een stream speelt, laadt de overlay hem nog (tot ±0,9 s later).
    Daarna zit hij in het geheugen en klopt het tot op ±0,06 s.
  · komen 2 shout-outs binnen dezelfde seconde binnen, dan kan de volgorde in de API anders zijn dan op de
    overlay (de API-tijden verschillen dan maar een paar honderd ms).

EXACTE TIJDEN (versie 3.23) – gelezen uit de overlay-code van DonateWave zelf:
  · vóór elke special-animatie wacht de overlay 1200 ms (video en geluid voorbereiden)
  · de extra sound start precies op het einde van de animatievideo (gemeten op de ms):
      Desert King 10,560 s · Royal Voyage 6,374 s · Golden Rush 7,053 s · Starlight Kingdom 6,060 s
      Spiky Groove 7,500 s · Enchanted Hat 7,300 s · Inferno Heir 8,000 s · Lion's Crown 7,766 s
      Acorn Fortune 6,600 s · Cosmic Launch 7,366 s
  · stuurt iemand dezelfde shout-out vaker (quantity, bijv. ×2), dan speelt de sound 2 keer → de show ook 2 keer
  · na de sound leest DonateWave naam en bedrag voor (TTS), de alert duurt minstens 2,2 s, daarna nog 500 ms
  · "Licht later laten beginnen dan de tip" is nu alleen nog de tijd tot je overlay de tip ontvangt (250 ms)
  · overlay-testtips (Test-knop bij Speciale shout-outs) komen nu ook via de API binnen: daarmee kun je testen

De wachtrij van de overlay (versie 3.21), zoals DonateWave hem afspeelt:
  · na elke sound blijven naam en bedrag nog even in beeld ("Naam en bedrag blijven na de sound in beeld",
    standaard 2000 ms: sound van 50 s → tot 52 s in beeld). Pas daarna begint de volgende alert/show.
  · stuurt iemand 2 (of meer) shout-outs vlak na elkaar, dan speelt de overlay eerst alle animaties achter
    elkaar en daarna pas de sounds. De app ziet de tweede via de API binnenkomen terwijl de animaties nog lopen
    en schuift de eerste show op tot na de laatste animatie; daarna sound 1 → naam/bedrag → sound 2.
  · tussen twee animaties zit nog wat tijd (laden, naam/bedrag): "Bij 2+ shout-outs tegelijk: extra tijd tussen
    de animaties" (standaard 1000 ms, versie 3.22). Telt alleen bij 2 of meer, dus één shout-out blijft kloppen.
  · afstellen: eerst met één shout-out "Licht later laten beginnen" goed zetten, daarna met 2 tegelijk dit veld.
Per special afstellen (versie 3.20): DonateWave-kaart › "Shout-outs afstellen": per special na hoeveel seconden
de extra sound begint (− / + per 0,1 s, of zelf invullen). Begint de animatie op stream later of duurt hij langer
dan de video (laden, naam/bericht in beeld), zet die special dan hoger. "Gemeten tijden terugzetten" zet alles
terug op de gemeten videoduur.
De Test-knoppen bij Speciale shout-outs sturen alleen naar je overlay, niet naar koppelingen. Testen met de
app kan alleen via Koppelingen › "Test versturen" (€ 5, speelt de Spiky Groove-regel niet: dat is geen shout-out).

Status zien: links in de zijbalk staat "DonateWave" met een lampje – groen = verbonden, oranje = verbinden of
opnieuw proberen, rood = fout (klik erop voor uitleg), grijs = uit.
Verander je in DonateWave › Alert-effecten welk geluid bij een bedrag hoort? Kies dan bij Regels dezelfde
♪-show voor dat bedrag (DonateWave stuurt niet mee welk geluid er speelt, alleen het bedrag).
Aanpassen: Regels › type "DonateWave-tip", bedragen in euro's.
Hoe het werkt (volgens https://www.donatewave.nl/developers): de app vraagt elke 2 seconden
GET /api/v1/events op met de sleutel in de Authorization-header (nooit in de URL), bewaart de cursor pas
als een tip verwerkt is, negeert dubbele en onbekende berichten en speelt tips ouder dan 3 minuten niet
meer af (bijv. van toen de app uit stond). Bij te veel verzoeken wacht hij 60 s; bij een geweigerde of
verlopen sleutel (na 90 dagen) stopt hij en zie je uitleg in het Error Center.
De sleutel wordt versleuteld opgeslagen (Windows DPAPI). Is je sleutel ergens gedeeld (bijv. in een chat
of screenshot)? Maak dan een nieuwe koppeling en trek de oude in.
In Spotify-modus en met Safe Mode aan worden tips genegeerd (testtips spelen wel).

3h. VERBINDINGSDETAILS / FOUTCODES (versie 3.17)
Error Center › onderaan "Verbindingsdetails": per verbinding het API-adres, de laatste HTTP-status
(bijv. "HTTP 200 OK", "HTTP 401 Unauthorized", "HTTP 503 Service Unavailable" of "geen antwoord"),
de responstijd, het antwoord van de server bij een fout, de cursor, de laatste 10 verzoeken naar DonateWave,
de BetterTok-status/adres en je lampen. Ververst elke seconde. "Kopiëren" zet alles op het klembord
(sleutels alleen gemaskeerd) om door te sturen. Fouten komen ook in app.log in je instellingenmap.

3g. WACHTRIJ: SHOWS ACHTER ELKAAR (versie 3.16)
Standaard spelen shows nu precies in de volgorde waarin ze binnenkomen en onderbreekt niets een lopende show,
net als de sounds in BetterTok en DonateWave: eerst Titanium helemaal, dan pas de volgende.
Instellingen › Wachtrij › "Shows achter elkaar afspelen" (uitzetten = oude gedrag met prioriteiten).
Lopen sound en licht na een paar shows uit elkaar, zet dan "Pauze tussen twee lichtshows" op dezelfde pauze
die BetterTok tussen sounds houdt (0 als BetterTok direct doorgaat).

4. GEWIJZIGDE BESTANDEN
-----------------------
Vervangen:
  App.xaml, App.xaml.cs             design system laden, opstarten, foutafhandeling, één exemplaar
  MainWindow.xaml, .xaml.cs         nieuwe shell: zijbalk, bovenbalk, meldingen, dialogen, Stream Mode
  StreamLightControl2.csproj        versie 3.0.0, exe-naam StreamLightControl, testmap uitgesloten
  README-NL.txt                     dit bestand
Verplaatst en uitgebreid:
  Models/AppSettings.cs   → Core/Models/AppSettings.cs (nieuw formaat + migratie van 2.0)
  Models/EffectRule.cs    → Core/Models/EventTrigger.cs + LightShow.cs (regel = koppeling + show)
  Services/GoveeLanClient.cs → Core/Services/GoveeLanClient.cs (zie verbeteringen hieronder)
Nieuw:
  Core/Infrastructure/ObservableObject.cs
  Core/Models/LightShow.cs, EventTrigger.cs, StreamEvent.cs, RuntimeModels.cs
  Core/Services/AutomationController.cs, AutomationQueue.cs, BetterTokClient.cs,
                BetterTokEventParser.cs, ErrorCenter.cs, HistoryStore.cs, LightShowEngine.cs,
                Presets.cs, RuleEngine.cs, SecretProtector.cs, SettingsStore.cs
  Themes/Theme.xaml (kleuren, typografie), Themes/Controls.xaml (alle componentstijlen)
  Controls/ColorPicker.cs, TimelineView.cs, Converters.cs
  UI/UiState.cs, Ui.cs, QuickActions.cs, Options.cs
  Views/*.xaml(.cs) – 12 pagina's
  Tests/ – testrunner met nagebootste BetterTok-server en nagebootste Govee-lamp

Gerepareerd ten opzichte van 2.0:
  - Geen automatische reconnect → nu met oplopende wachttijd (1–30 s), niet bij geweigerde key.
  - Effecten liepen door elkaar → echte wachtrij met prioriteiten.
  - Eerste passende regel won, giftnaam werd in de hele JSON gezocht → één koppeling per event.
  - Gift-combo's speelden meerdere keren → combo wordt pas aan het eind verwerkt, coins één keer geteld.
  - Coins werden niet berekend → diamondCount × repeatCount (Local) / gift_total_value (Cloud).
  - Discovery negeerde alles behalve H619E en zette ReuseAddress te laat → alle modellen, juiste volgorde,
    zoekbericht via elke netwerkkaart.
  - Geen statuscontrole → devStatus laat zien of LAN Control echt antwoordt.
  - Cloud Mode / API-key ontbrak → toegevoegd volgens de officiële BetterTok-documentatie.
  - Poortscan 21213–21222 duurde op Windows tot ~20 s → nu alle poorten tegelijk.


5. WAT ER DAADWERKELIJK GETEST IS
---------------------------------
Uitvoeren:  in de map Tests →  dotnet run      (61 tests, ± 2 minuten, draait ook zonder lampen)

Getest tegen een nagebootste BetterTok-server en een nagebootste Govee-lamp die het officiële
Govee LAN-protocol spreekt (scan / devStatus / turn / brightness / colorwc):
  ✔ Volledige workflow: BetterTok-follow → regel → wachtrij → lamp knippert 2× blauw → lamp terug naar wit
  ✔ Gift-combo (3 tussenberichten + eind) → precies één effect, 120 coins één keer geteld
  ✔ Safe Mode: event wordt gelogd, er gaat niets naar de lamp
  ✔ Testknop van een koppeling speelt echt af; tests tellen niet mee in de statistiek
  ✔ BetterTok Local: poortscan vindt de juiste poort, events komen binnen, automatisch opnieuw verbinden
  ✔ BetterTok Local: duidelijke foutmelding + oplossing als BetterTok niet draait
  ✔ BetterTok Cloud: Bearer-key, connected-frame, ping/pong, cloud-gift
  ✔ BetterTok Cloud: UNAUTHORIZED → duidelijke fout en géén eindeloze reconnect
  ✔ Parser: Local- en Cloud-formaat, follow/gift/like/share/chat/member/social, control frames
  ✔ Regels: coin-bereiken, specifieke gift gaat voor, uitgeschakeld, dubbel bericht, follow één keer per
    kijker, bundelen, like-drempel, cooldown, combo, Cloud-naschot, wachtrijlimiet, overlap-/gatwaarschuwing
  ✔ Govee: zoeken, status opvragen, kleur en helderheid, geen antwoord → netjes "niet bereikbaar"
  ✔ Engine: flitsen met juiste timing (≈1 s), vloeiende overgangen, herstellen van de vorige toestand
  ✔ Wachtrij: prioriteit, verwijderen, verplaatsen, prioriteit wijzigen, leegmaken, volle wachtrij,
    overslaan, onderbreken door belangrijker effect, alles stoppen
  ✔ Police knippert rood/blauw met 1 commando per fase, zonder trage uit/aan-commando's, op tijd
  ✔ Alle 32 song-shows: lengte gelijk aan de sound (±0,1 s), geen stap korter dan de strip aankan
  ✔ Gift-regels gelijk aan de BetterTok-actions (coin-drempels + 11 gifts met eigen sound), geen overlap of gaten
  ✔ Update van versie 15 → 16: nieuwe sounds erbij, sounds die er niet meer zijn weg, eigen regels blijven
  ✔ DonateWave (nep-server): Bearer-header, sleutel nooit in de URL, cursor pas na verwerken, dubbel/oud/onbekend genegeerd,
    401 stopt, 429 wacht, netwerkfout opnieuw met dezelfde cursor, test uit DonateWave speelt de juiste show
  ✔ Song "Turn on the microwave" volledig afgespeeld: elke stap binnen 24 ms van zijn beat
  ✔ Spotify: nummer uit de vensternaam (spelen / pauze / reclame / streepje in de titel)
  ✔ Spotify: elk nummer eigen kleuren, altijd hetzelfde, elke wissel ≥ 100° kleurverschil
  ✔ Spotify: beatdetectie vindt kicks op 150 BPM en negeert constante ruis en stilte
  ✔ Spotify-modus volledig: kleuren op de beat, nooit sneller dan 550 ms, nieuw nummer = nieuwe kleuren,
    pauze = zacht licht, Safe Mode = niets, BetterTok-events genegeerd, terug naar Stream stopt alles
  ✔ Spotify: tempo 150 BPM herkend, drop na een breakdown herkend (precies één keer), vertraging uit
    tikken uitgerekend (180 ms)
  ✔ Spotify met "licht eerder sturen" 150 ms: de wissels gaan echt ±150 ms vóór de beat de deur uit
  ✔ Spotify-modus: flashbangs en zwarte stukjes in harde stukken, elke kleurwissel ≥ 100° contrast
  ✔ Direct springen: razer-pakketten byte-voor-byte gelijk aan OpenRGB (BB 00 01 B1 01 0A), juiste lengte
    en XOR-controlecode; show gaat via realtime-protocol, herstel weer via colorwc
  ✔ Club-versies: elke beat, ≥ 4 flashbangs en ≥ 3 blackouts per song, ≥ 6 kleuren, ≥ 100° contrast,
    nooit sneller dan 4 per seconde, even lang als de sound; club-presets passen binnen 300 ms
  ✔ App kiest zelf club of veilig bij het wijzigen van de snelheidslimiet; eigen aanpassingen blijven
  ✔ Lichtshow-afbeelding gemaakt door Claude (Python) wordt door de app gelezen; importeren vervangt
    de bestaande show met behoud van de gift-koppeling; export → import geeft exact dezelfde stappen;
    koppelen aan 1234 coins gaat vóór het bereik 999–1499; kapotte bestanden → duidelijke fout
  ✔ Spotify zonder geluidsmeting: rustig vast tempo + melding; stijlen Rustig en Eén kleur
  ✔ Opslag: settings.json van 2.0 wordt correct omgezet, alles blijft bewaard na herstart, automatisch
    opslaan, beschadigd bestand → back-up + waarschuwing, API-key niet leesbaar opgeslagen

Interface (WPF) – gecontroleerd zonder Windows:
  ✔ Alle C#-code van de interface compileert foutloos tegen de echte WPF-API (referentiebronnen van
    github.com/dotnet/wpf). Daarbij is een echte compileerfout gevonden en opgelost.
  ✔ Alle 159 knop-/event-koppelingen in XAML hebben een bestaande handler met de juiste handtekening.
  ✔ Elk XAML-element en elke eigenschap bestaat echt op dat type; alle 103 stijl-/kleursleutels bestaan.


6. EERLIJK: WAT NOG NIET GETEST IS / BEKENDE BEPERKINGEN
--------------------------------------------------------
  - De interface is NIET op Windows gestart of bekeken. Bouw de app en loop de stappen in hoofdstuk 2
    langs. Ziet iets er vreemd uit of geeft Visual Studio een fout? Stuur een screenshot.
  - Niet getest op jouw fysieke H619E. Het protocol klopt met de officiële Govee LAN API, maar hoe snel
    jouw strip knippert verschilt per model. Knippert hij traag of met een fade? Instellingen →
    Verlichting → "Kleur naar zwart". Mist hij stappen? Zet de pauze tussen commando's hoger.
  - BetterTok-documentatie noemt voor Local Mode alleen de namen chat, member, subscribe, roomUser en gift.
    Voor follow, like en share zegt de documentatie expliciet "niet aannemen". De app herkent de gangbare
    namen (follow, like, share en "social" met displayType). Kijk na je eerste LIVE op de BetterTok-
    pagina bij "Ontvangen berichten" of follows/likes/shares herkend worden. Stuur me anders een
    voorbeeld van zo'n bericht, dan pas ik de herkenning aan.
  - Cloud Mode is getest tegen een nagebootste server volgens de documentatie, niet tegen de echte
    BetterTok-server (daarvoor is een Creator-abonnement nodig).
  - De hele strip krijgt één kleur tegelijk. Per-segment RGBIC-effecten zitten niet in de standaard
    LAN API en zijn niet ingebouwd.
  - Spotify-modus is getest met een nagebootste Spotify en nagebootst geluid, niet met de echte
    Spotify-app en Windows-geluidsmeter (die bestaan alleen op Windows). Reageert het niet op de beat?
    Zet de gevoeligheid hoger en kijk of het Beat-bolletje knippert.
  - "Direct springen" gebruikt het niet-officiële realtime-protocol van Govee. OpenRGB ondersteunt het
    op de H619A (zelfde serie als de H619E), maar of jouw strip het kent zie je pas met de test.
  - Statistieken op het dashboard gelden per sessie (vanaf het starten van de app) en beginnen op 0.
