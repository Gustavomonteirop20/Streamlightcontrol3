# Stream Light Control – afspraken voor Claude

Windows 11-app (WPF, .NET 8) van een TikTok-streamer. TikTok LIVE-events (via BetterTok) en DonateWave-tips sturen
Govee-lampen aan met lichtshows. **De gebruiker schrijft Nederlands; de hele app (UI, teksten, README) is Nederlands.**

## Harde regels
- **Begin nooit opnieuw met een leeg project.** Altijd verder bouwen op deze code.
- **Eventstructuren (BetterTok, DonateWave) niet veranderen zonder de officiële documentatie te controleren.**
  Parsers: `Core/Services/BetterTokEventParser.cs`, `Core/Services/DonateWaveClient.cs`.
- **Geheimen nooit in de code, een zip of GitHub** (zoals de DonateWave-sleutel `dw_evt_…`). Sleutels staan
  versleuteld in de instellingen (`SecretProtector`). Controleer voor elke zip of commit dat er geen sleutel in staat.
- **Een update voor iedereen (GitHub-release) alleen na een expliciet "ja" van de gebruiker**, elke keer opnieuw.
  Release: `gh api -X POST repos/Gustavomonteirop20/streamlightcontrol3/actions/workflows/release.yml/dispatches -f ref=main -f 'inputs[version]=X.Y.Z'`
  (tags pushen is geblokkeerd). Gewone pushes naar `main` publiceren niets.
- Een cloud-frontend mag nooit rechtstreeks lokale UDP-apparaten of localhost-WebSockets bedienen.
- Lampen: niet snel laten knipperen (kijkers krijgen er last van). Flitsen altijd **minder dan 3 per seconde**,
  geen witte stroboscoop. Tests bewaken dit.
- De gebruiker wil "zonder fouten": na elke wijziging bouwen én alle tests draaien.

## Bouwen en testen
- Tests (kernlogica, ook op Linux): `dotnet run --project Tests/StreamLightControl2.Tests.csproj` → moet "N/N tests geslaagd" geven.
- App bouwen (Windows): `dotnet build StreamLightControl2.csproj -c Release`
- Losse .exe voor de gebruiker: `MaakExe.bat` (zet hem op het bureaublad in "Stream Light Control").
- GitHub-workflow **Bouwcontrole** (`.github/workflows/build.yml`) draait bij elke push op Windows: WPF-build, alle tests,
  `Tests/SoundCheck` (mp3/m4a lezen via NAudio) en `Tests/ThemeCheck` (echte stijlen met elk thema).
- Bij elke nieuwe versie: `<Version>` in `StreamLightControl2.csproj` ophogen en een stukje bovenaan `README-NL.txt` schrijven.
- Instellingen wijzigen van vorm → nieuwe migratie in `SettingsStore.Migrate` en `AppSettings.Version` ophogen (nu 33).
  Eigen regels/shows van de gebruiker blijven altijd behouden.
- Nieuwe Core-bestanden die bestanden lezen/schrijven: `using System.IO;` toevoegen (WPF heeft die niet standaard).
- In knopstijlen wordt `Tag` als icoonletter getoond: gebruik `Tag` nooit voor data.

## Opbouw
- `Core/Models` – instellingen (`AppSettings`), shows (`LightShow`, `LightStep`), koppelingen (`EventTrigger`).
- `Core/Services`
  - `AutomationController` – centrale regie; `RuleEngine` – welke koppeling per event (één show per event, dedupe,
    cooldown, binnenkomst 1× per live); `AutomationQueue` – shows achter elkaar, wacht op de sound (HoldMs).
  - `LightShowEngine` – speelt shows af: precies (`PlayExact`), rustig/festival (`CalmShow`) of klassiek; timing met
    1 ms Windows-timer + exact aftellen.
  - `GoveeLanClient` – Govee LAN (UDP 4001/4002/4003), direct-zones (razer) en segmenten (ptReal).
  - `SongShows` – song-shows per dono-sound/binnenkomst, gemeten op de beat; `Presets` – standaardregels en shows.
  - `ShowGenerator` – showmaker: maakt van een sound een show (kicks, groove/melodie/opbouw/drop).
  - `OverlayServer`/`OverlayPages`/`TikTokPages` – kerst-overlays (DonateWave en TikTok) op 127.0.0.1:7531/7532.
  - `AppThemes` – thema's standaard/kerst/herfst/zomer/auto (toegepast in `UI/ThemeApplier` bij het opstarten).
- `Views` – pagina's; `UI` – UI-hulpjes (`UiState`, `SoundDecoder`, `ThemeApplier`).
- Gegevens van de gebruiker: `%LocalAppData%\StreamLightControl2` (settings.json, history, tiktok.json, live-state.json).

## Setup van de streamer
- Lampen: 2× Govee H619E RGBIC Pro (LAN Control aan). Op stream zijn alleen **direct-zones 11–20** te zien.
- Eventbron: BetterTok (Local mode, ws://127.0.0.1:21213 e.v.). DonateWave voor tips/shout-outs.
- Binnenkomst-shows: thomas (gezellig kinderliedje), diego, martin (C4-sound), nickstreams2 (zoals Nick z'n video).
- Shows moeten precies op de beat lopen; de volgende show pas als de sound van de vorige klaar is.
