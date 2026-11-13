using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamLightControl2.Core.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string ZipUrl, string? Sha256Url, DateTime? PublishedAt);

/// <summary>
/// Automatische updates via GitHub Releases.
/// 1. Controleert https://api.github.com/repos/{repo}/releases/latest
/// 2. Downloadt het zip-bestand (…-win-x64.zip) en controleert de SHA-256 (…-win-x64.zip.sha256)
/// 3. Pakt uit en laat een klein PowerShell-script de bestanden vervangen zodra de app is afgesloten, daarna herstart.
/// Instellingen staan in %LocalAppData% en worden nooit aangeraakt.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Vaste GitHub-repository voor updates ("eigenaar/naam"). Leeg = updates uit tot er een is ingevuld.</summary>
    public const string DefaultRepo = "Gustavomonteirop20/Streamlightcontrol3";
    public const string AssetSuffix = "-win-x64.zip";

    private readonly HttpClient _http;
    private readonly string _workDir;

    /// <summary>Alleen voor tests: ander API-adres dan https://api.github.com.</summary>
    public string ApiBase { get; init; } = "https://api.github.com";

    public UpdateService(string workDirectory, HttpClient? http = null)
    {
        _workDir = Path.Combine(workDirectory, "updates");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamLightControl-Updater");
    }

    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    public static Version? ParseTag(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>Geeft de nieuwste release terug als die nieuwer is dan <paramref name="current"/>, anders null.</summary>
    public async Task<UpdateInfo?> CheckAsync(string repo, Version current, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
            throw new InvalidOperationException("Er is nog geen update-adres ingesteld.");
        using var resp = await _http.GetAsync($"{ApiBase}/repos/{repo.Trim()}/releases/latest", token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Geen release gevonden op GitHub ({repo}). Is de repository openbaar en is er al een versie gepubliceerd?");
        if ((int)resp.StatusCode == 403)
            throw new InvalidOperationException("GitHub weigert tijdelijk (te veel verzoeken). Probeer het over een uur opnieuw.");
        resp.EnsureSuccessStatusCode();
        var info = ParseRelease(await resp.Content.ReadAsStringAsync(token));
        return info != null && info.Version > current ? info : null;
    }

    public static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = ParseTag(tag);
        if (version == null || !r.TryGetProperty("assets", out var assets)) return null;
        string? zip = null, sha = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            if (name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) zip = url;
            else if (name.EndsWith(AssetSuffix + ".sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (zip == null) return null;
        DateTime? published = r.TryGetProperty("published_at", out var pa) && pa.TryGetDateTime(out var dt) ? dt : null;
        var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, notes, zip, sha, published);
    }

    /// <summary>Downloadt, controleert en pakt uit. Geeft de map met de nieuwe app-bestanden terug.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var dir = Path.Combine(_workDir, info.Version.ToString());
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "update.zip");

        using (var resp = await _http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(token);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, token)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (total > 0) progress?.Report(done / (double)total);
            }
        }

        if (info.Sha256Url != null)
        {
            var expected = (await _http.GetStringAsync(info.Sha256Url, token)).Trim().Split(' ', '\t', '\n', '\r')[0].ToLowerInvariant();
            string actual;
            await using (var fs = File.OpenRead(zipPath)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, token)).ToLowerInvariant();
            if (expected != actual)
            {
                Directory.Delete(dir, true);
                throw new InvalidDataException("De download is beschadigd (controlecode klopt niet). De update is niet geïnstalleerd.");
            }
        }

        var files = Path.Combine(dir, "files");
        ZipFile.ExtractToDirectory(zipPath, files);
        // Zip kan een submap bevatten: zoek de map met de exe
        var exe = Directory.GetFiles(files, "StreamLightControl.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidDataException("De update bevat geen StreamLightControl.exe.");
        return Path.GetDirectoryName(exe)!;
    }

    /// <summary>
    /// Kan de app zichzelf bijwerken? Niet vanuit Visual Studio (bin\Debug) en niet in een map zonder schrijfrechten.
    /// </summary>
    public static (bool ok, string reason) CanSelfUpdate(string? installDir)
    {
        if (string.IsNullOrEmpty(installDir)) return (false, "Installatiemap onbekend.");
        if (installDir.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (false, "Je start de app vanuit Visual Studio. Updates werken in de .exe-versie (MaakExe.bat).");
        try
        {
            var probe = Path.Combine(installDir, ".update-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return (true, "");
        }
        catch { return (false, "Geen schrijfrechten in de installatiemap. Zet de app bijvoorbeeld op je bureaublad."); }
    }

    /// <summary>Schrijft het vervangscript. Het wacht tot de app dicht is, kopieert de nieuwe bestanden en start de app opnieuw.</summary>
    public static string WriteApplyScript(string newFilesDir, string installDir, int processId, string exeName, string scriptDir)
    {
        Directory.CreateDirectory(scriptDir);
        var script = Path.Combine(scriptDir, "apply-update.ps1");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$src = {Q(newFilesDir)}");
        sb.AppendLine($"$dst = {Q(installDir)}");
        sb.AppendLine($"$exe = Join-Path $dst {Q(exeName)}");
        sb.AppendLine($"$log = Join-Path {Q(scriptDir)} 'update.log'");
        sb.AppendLine($"try {{ Wait-Process -Id {processId} -Timeout 60 -ErrorAction SilentlyContinue }} catch {{}}");
        sb.AppendLine("Start-Sleep -Milliseconds 500");
        sb.AppendLine("$ok = $false");
        sb.AppendLine("for ($i = 0; $i -lt 10 -and -not $ok; $i++) {");
        sb.AppendLine("  try { Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force; $ok = $true }");
        sb.AppendLine("  catch { Add-Content $log (\"Poging $i mislukt: \" + $_); Start-Sleep -Seconds 1 }");
        sb.AppendLine("}");
        sb.AppendLine("if ($ok) { Add-Content $log ((Get-Date).ToString('s') + ' update geïnstalleerd') }");
        sb.AppendLine("Start-Process -FilePath $exe -ArgumentList '--restart','--updated'");
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(true));
        return script;
    }

    /// <summary>Start het vervangscript (verborgen). De app moet zich daarna direct afsluiten.</summary>
    public static void LaunchApplyScript(string script)
    {
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}
