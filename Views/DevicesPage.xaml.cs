using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using StreamLightControl2.Controls;
using StreamLightControl2.Core.Models;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2.Views;

public partial class DevicesPage : UserControl, IPageActivated
{
    private UiState S => App.State;
    private AutomationController C => App.Controller;
    private GoveeDeviceConfig? _selected;
    private bool _loadingSlider;

    public DevicesPage()
    {
        InitializeComponent();
        DataContext = S;
        DirectSwitch.DataContext = S;   // het bedieningspaneel heeft de lamp als DataContext; deze schakelaar hoort bij de instellingen
        S.Settings.Devices.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
        if (S.Settings.Devices.Count > 0) DeviceList.SelectedIndex = 0;
    }

    public void OnActivated()
    {
        UpdateEmpty();
        if (_selected != null) _ = RefreshStatus(_selected, quiet: true);
    }

    private void UpdateEmpty() =>
        EmptyState.Visibility = S.Settings.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------ zoeken en toevoegen

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        await Ui.Busy(sender, "Zoeken…", async () =>
        {
            List<DiscoveredDevice> found;
            try { found = await C.Govee.DiscoverAsync(3000); }
            catch (InvalidOperationException ex)
            {
                C.Errors.Report("gv.port", ex.Message);
                throw new InvalidOperationException("Zoeken lukt niet: poort 4002 is bezet. Zie Error Center.");
            }
            // Bekende apparaten bijwerken (IP kan veranderd zijn)
            var updated = 0;
            foreach (var f in found)
            {
                var known = S.Settings.Devices.FirstOrDefault(d => !string.IsNullOrEmpty(d.Mac) && d.Mac.Equals(f.Mac, StringComparison.OrdinalIgnoreCase))
                            ?? S.Settings.Devices.FirstOrDefault(d => d.Ip == f.Ip);
                if (known == null) continue;
                if (known.Ip != f.Ip) { known.Ip = f.Ip; updated++; }
                if (string.IsNullOrEmpty(known.Mac)) known.Mac = f.Mac;
                if (string.IsNullOrEmpty(known.Sku)) known.Sku = f.Sku;
                known.Online = true;
                known.LastSeen = DateTime.Now;
            }
            var fresh = found.Where(f => S.Settings.Devices.All(d => d.Ip != f.Ip && (string.IsNullOrEmpty(d.Mac) || !d.Mac.Equals(f.Mac, StringComparison.OrdinalIgnoreCase)))).ToList();
            FoundList.ItemsSource = fresh;
            FoundPanel.Visibility = fresh.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FoundTitle.Text = fresh.Count == 1 ? "1 nieuw apparaat gevonden" : $"{fresh.Count} nieuwe apparaten gevonden";
            S.RefreshDevices();

            if (found.Count == 0)
            {
                C.Errors.Report("gv.notfound");
                Ui.Toast("Geen Govee-apparaten gevonden. Bekijk het Error Center voor tips.", ToastKind.Warning);
            }
            else
            {
                C.Errors.Resolve("gv.notfound");
                Ui.Toast($"{found.Count} apparaat/apparaten gevonden" + (updated > 0 ? $", {updated} IP-adres bijgewerkt" : ""));
            }
        });
    }

    private void AddFound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not DiscoveredDevice f) return;
        var d = new GoveeDeviceConfig { Ip = f.Ip, Mac = f.Mac, Sku = f.Sku, Name = string.IsNullOrEmpty(f.Sku) ? "Govee-apparaat" : "Govee " + f.Sku, Online = true, LastSeen = DateTime.Now };
        S.Settings.Devices.Add(d);
        if (FoundList.ItemsSource is List<DiscoveredDevice> list)
        {
            list.Remove(f);
            FoundList.ItemsSource = null;
            FoundList.ItemsSource = list;
            FoundPanel.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        DeviceList.SelectedItem = d;
        S.RefreshDevices();
        Ui.Toast($"{d.Name} toegevoegd");
        _ = RefreshStatus(d, quiet: true);
    }

    private void ShowManual_Click(object sender, RoutedEventArgs e) { ManualForm.Visibility = Visibility.Visible; ManualIp.Focus(); }
    private void HideManual_Click(object sender, RoutedEventArgs e) => ManualForm.Visibility = Visibility.Collapsed;

    private async void AddManual_Click(object sender, RoutedEventArgs e)
    {
        var ip = ManualIp.Text.Trim();
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            Ui.Toast("Vul een geldig IPv4-adres in, bijvoorbeeld 192.168.1.50.", ToastKind.Warning);
            return;
        }
        if (S.Settings.Devices.Any(d => d.Ip == ip)) { Ui.Toast("Dit IP-adres staat al in de lijst.", ToastKind.Warning); return; }
        var device = new GoveeDeviceConfig { Ip = ip, Name = string.IsNullOrWhiteSpace(ManualName.Text) ? "Govee-apparaat" : ManualName.Text.Trim(), Sku = "H619E" };
        S.Settings.Devices.Add(device);
        ManualForm.Visibility = Visibility.Collapsed;
        ManualIp.Text = "";
        DeviceList.SelectedItem = device;
        S.RefreshDevices();
        Ui.Toast($"{device.Name} toegevoegd – verbinding wordt getest…", ToastKind.Info);
        await RefreshStatus(device, quiet: false);
    }

    // ------------------------------------------------------------ per apparaat

    private static GoveeDeviceConfig? Of(object sender) => (sender as Button)?.CommandParameter as GoveeDeviceConfig;

    private async void TestConn_Click(object sender, RoutedEventArgs e)
    {
        var d = Of(sender);
        if (d == null) return;
        await Ui.Busy(sender, "Testen…", () => RefreshStatus(d, quiet: false));
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        var d = Of(sender);
        if (d == null) return;
        await Ui.Busy(sender, "Zoeken…", async () =>
        {
            var msg = await C.ReconnectDeviceAsync(d);
            Ui.Toast(msg, d.Online == true ? ToastKind.Success : ToastKind.Warning);
            if (ReferenceEquals(d, _selected)) ShowStatus(d);
        });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var d = Of(sender);
        if (d == null) return;
        var name = await Ui.Prompt("Apparaat hernoemen", "Nieuwe naam:", d.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        d.Name = name;
        Ui.Toast("Hernoemd");
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var d = Of(sender);
        if (d == null) return;
        if (!await Ui.Confirm("Apparaat verwijderen?", $"‘{d.Name}’ ({d.Ip}) doet daarna niet meer mee met lichtshows.", "Verwijderen", danger: true)) return;
        S.Settings.Devices.Remove(d);
        S.RefreshDevices();
        Ui.Toast("Apparaat verwijderd");
    }

    private async Task RefreshStatus(GoveeDeviceConfig d, bool quiet)
    {
        var status = await C.TestDeviceAsync(d);
        if (ReferenceEquals(d, _selected)) ShowStatus(d);
        if (quiet) return;
        if (status != null) Ui.Toast($"{d.Name} antwoordt – LAN Control is actief");
        else
        {
            C.Errors.Report("gv.noresponse", $"{d.Name} ({d.Ip})");
            Ui.Toast($"{d.Name} geeft geen antwoord. Staat LAN Control aan? Zie Error Center.", ToastKind.Warning);
        }
    }

    // ------------------------------------------------------------ bedieningspaneel

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected != null) _selected.PropertyChanged -= SelectedChanged;
        _selected = DeviceList.SelectedItem as GoveeDeviceConfig;
        ControlPanel.DataContext = _selected;
        ControlPanel.Visibility = _selected == null ? Visibility.Collapsed : Visibility.Visible;
        NoDeviceText.Visibility = _selected == null ? Visibility.Visible : Visibility.Collapsed;
        if (_selected == null) return;
        _selected.PropertyChanged += SelectedChanged;
        ShowStatus(_selected);
        if (_selected.Online == null) _ = RefreshStatus(_selected, quiet: true);
    }

    private void SelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName is nameof(GoveeDeviceConfig.Status) or nameof(GoveeDeviceConfig.Online)) && sender is GoveeDeviceConfig d)
            Dispatcher.BeginInvoke(() => ShowStatus(d));
    }

    private void ShowStatus(GoveeDeviceConfig d)
    {
        var s = d.Status;
        if (d.Online == false)
        {
            StatusLine1.Text = "Geen antwoord";
            StatusLine2.Text = "LAN Control uit, lamp zonder stroom of ander IP-adres.";
            StatusSwatch.Background = Ui.Res("InputBrush");
            return;
        }
        if (s == null)
        {
            StatusLine1.Text = "Status onbekend";
            StatusLine2.Text = "Klik op ‘Status ophalen’.";
            StatusSwatch.Background = Ui.Res("InputBrush");
            return;
        }
        StatusLine1.Text = s.On ? $"Aan · {s.Brightness}% · {s.ColorHex}" : "Uit";
        StatusLine2.Text = "LAN Control actief · bijgewerkt " + Ui.Ago(d.LastSeen);
        StatusSwatch.Background = s.On ? HexToBrushConverter.Get(s.ColorHex) : Ui.Res("InputBrush");
        _loadingSlider = true;
        BrightnessSlider.Value = Math.Max(1, s.Brightness);
        _loadingSlider = false;
        if (s.On && s.R + s.G + s.B > 0) DevicePicker.SelectedHex = s.ColorHex;
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) await Ui.Busy(sender, "Ophalen…", () => RefreshStatus(_selected, quiet: false));
    }

    private string[] One => _selected == null ? Array.Empty<string>() : new[] { _selected.Ip };

    private async Task Send(object sender, Func<Task> action, string ok)
    {
        if (_selected == null) return;
        await Ui.Busy(sender, "Bezig…", async () =>
        {
            try { await action(); C.Errors.Resolve("gv.send"); }
            catch (Exception ex) { C.Errors.Report("gv.send", ex.Message); throw; }
            await Task.Delay(250);
            await C.TestDeviceAsync(_selected);
        }, ok);
    }

    private async void On_Click(object sender, RoutedEventArgs e) => await Send(sender, () => C.Engine.PowerAllAsync(One, true), "Aan-commando verzonden");
    private async void Off_Click(object sender, RoutedEventArgs e) => await Send(sender, () => C.Engine.PowerAllAsync(One, false), "Uit-commando verzonden");

    private async void ApplyColor_Click(object sender, RoutedEventArgs e) =>
        await Send(sender, () => C.Engine.SetStaticAsync(One, DevicePicker.SelectedHex, (int)BrightnessSlider.Value), "Kleur verzonden");

    private void Brightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessValue != null) BrightnessValue.Text = $"{(int)e.NewValue}%";
    }

    private async void Brightness_DragCompleted(object sender, DragCompletedEventArgs e) => await ApplyBrightness();
    private async void Brightness_MouseUp(object sender, MouseButtonEventArgs e)
    {
        await Task.Delay(30); // laat de slider eerst zijn nieuwe waarde zetten
        await ApplyBrightness();
    }

    private int _lastSentBrightness = -1;
    private async Task ApplyBrightness()
    {
        if (_selected == null || _loadingSlider) return;
        var value = (int)BrightnessSlider.Value;
        if (value == _lastSentBrightness) return;
        _lastSentBrightness = value;
        try { await C.Engine.BrightnessAllAsync(One, value); }
        catch (Exception ex) { C.Errors.Report("gv.send", ex.Message); Ui.Toast("Helderheid niet verzonden: " + ex.Message, ToastKind.Error); }
    }

    // ---------------------------------------------------------------- Zones-test

    private CancellationTokenSource? _zoneCts;

    private static (byte, byte, byte) Hsv(double h)
    {
        var c = 1.0; var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private async Task SendZones(GoveeDeviceConfig dev, bool segment, IReadOnlyList<(byte, byte, byte)> zones, CancellationToken token)
    {
        if (segment) await C.Govee.SegmentColorsAsync(dev.Ip, zones, token);
        else await C.Govee.DirectZonesAsync(dev.Ip, zones, token);
    }

    private async Task ZoneTest(Func<GoveeDeviceConfig, bool, int, CancellationToken, Task> body, bool segment)
    {
        if (_selected == null) return;
        var dev = _selected;
        var count = segment ? Math.Min(dev.ZoneCount, 16) : dev.ZoneCount;
        _zoneCts?.Cancel();
        var cts = _zoneCts = new CancellationTokenSource();
        var wasPaused = C.Queue.Paused;
        C.Queue.StopCurrent();                       // geen shows tijdens de test
        try
        {
            if (segment) { await C.Govee.DirectModeAsync(dev.Ip, false, cts.Token); await C.Govee.PowerAsync(dev.Ip, true, cts.Token); }
            else await C.Govee.DirectModeAsync(dev.Ip, true, cts.Token);
            await body(dev, segment, count, cts.Token);
        }
        catch (OperationCanceledException) { ZoneNow.Text = "Test gestopt."; }
        catch (Exception ex) { ZoneNow.Text = "Fout: " + ex.Message; }
        finally
        {
            try { if (!segment) await C.Govee.DirectModeAsync(dev.Ip, false); } catch { }
            C.Engine.ForgetState();
            if (!wasPaused) C.Queue.Resume();
            if (_zoneCts == cts) _zoneCts = null;
        }
    }

    private async Task WalkZones(GoveeDeviceConfig dev, bool segment, int count, CancellationToken token)
    {
        dev.ZoneMethod = segment ? "segment" : "direct";
        for (var i = 1; i <= count; i++)
        {
            ZoneNow.Text = $"{(segment ? "Segment" : "Zone")} {i} van {count}";
            var zones = Enumerable.Range(1, count).Select(z => z == i ? ((byte)255, (byte)190, (byte)0) : ((byte)0, (byte)0, (byte)40)).ToList();
            await SendZones(dev, segment, zones, token);
            await Task.Delay(1800, token);
        }
        ZoneNow.Text = "Regenboog: elke zone een andere kleur";
        await SendZones(dev, segment, Enumerable.Range(0, count).Select(z => Hsv(360.0 * z / count)).ToList(), token);
        await Task.Delay(4000, token);
        ZoneNow.Text = "Klaar. Vul hieronder in welke nummers je op stream zag (bijv. 1-3, 18-20).";
    }

    private async void ZonesDirect_Click(object sender, RoutedEventArgs e) => await ZoneTest(WalkZones, segment: false);

    private async void ZonesSegment_Click(object sender, RoutedEventArgs e) => await ZoneTest(WalkZones, segment: true);

    private void ZonesStop_Click(object sender, RoutedEventArgs e) => _zoneCts?.Cancel();

    private async void ZonesShowVisible_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var visible = GoveeDeviceConfig.ParseZones(_selected.VisibleZones, _selected.ZoneCount);
        if (visible.Count == 0) { Ui.Toast("Vul eerst in welke zones je op stream ziet, bijv. 1-3, 18-20.", ToastKind.Warning); return; }
        await ZoneTest(async (dev, segment, count, token) =>
        {
            ZoneNow.Text = $"Alleen zones {dev.VisibleZones} branden (5 s)";
            await SendZones(dev, segment, Enumerable.Range(1, count).Select(z => visible.Contains(z) ? ((byte)255, (byte)170, (byte)40) : ((byte)0, (byte)0, (byte)0)).ToList(), token);
            await Task.Delay(5000, token);
            ZoneNow.Text = "Zag je precies de stukjes die op stream staan? Dan klopt het.";
        }, segment: _selected.ZoneMethod == "segment");
    }

    private async void DirectTest_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var ip = _selected.Ip;
        var lighting = C.Settings.Lighting;
        var before = lighting.DirectColor;
        (byte r, byte g, byte b)[] colors = { (255, 0, 0), (0, 40, 255), (0, 255, 40) };
        async Task Run(bool direct)
        {
            lighting.DirectColor = direct;
            C.Engine.ForgetState();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var k = 0; sw.ElapsedMilliseconds < 3600; k++)
            {
                var c = colors[k % colors.Length];
                await C.Engine.TestColorAsync(ip, c.r, c.g, c.b);
                var wait = (k + 1) * 600 - sw.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay((int)wait);
            }
        }
        await Ui.Busy(sender, "Bezig…", async () =>
        {
            App.Controller.Queue.SkipCurrent();
            await C.Engine.SetStaticAsync(One, "#000000", 100);
            Ui.Toast("1/2: DIRECT – springt de kleur meteen om?", ToastKind.Info);
            await Run(true);
            await C.Engine.SetStaticAsync(One, "#000000", 100);
            await Task.Delay(1200);
            Ui.Toast("2/2: GEWOON – nu met de fade van de strip", ToastKind.Info);
            await Run(false);
            lighting.DirectColor = before;
            await C.Engine.SetStaticAsync(One, "#FFFFFF", 60);
        });
        var yes = await Ui.Confirm("Sprong de kleur bij 1/2 meteen om?",
            "Bij stap 1 (direct) hoort je strip hard van rood naar blauw naar groen te springen. Bij stap 2 loopt het zacht over. " +
            "Zag je bij stap 1 helemaal niets gebeuren, dan kent jouw strip het directe protocol niet.", "Ja, direct aanzetten");
        lighting.DirectColor = yes;
        // Direct springen = de strip kan sneller: club-versies van de shows (±4 wissels per seconde)
        if (yes && lighting.MinChangeMs > 40) lighting.MinChangeMs = 40;   // direct = geen snelheidslimiet
        Ui.Toast(yes ? "Direct springen staat aan en de club-versies van je shows zijn gekozen. Mist je strip toch wissels? Doe de Snelheidstest."
                     : "Direct springen staat uit. Je strip gebruikt de gewone kleurwissel.", yes ? ToastKind.Success : ToastKind.Info);
    }

    private async void SpeedTest_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var ip = _selected.Ip;
        int[] rates = { 2, 3, 4, 5, 6, 8 };
        await Ui.Busy(sender, "Bezig…", async () =>
        {
            App.Controller.Queue.SkipCurrent();
            await C.Engine.SetStaticAsync(One, "#000000", 100);
            for (var r = 0; r < rates.Length; r++)
            {
                var rate = rates[r];
                Ui.Toast($"Stap {r + 1}/{rates.Length}: {rate} wissels per seconde", ToastKind.Info);
                var interval = 1000 / rate;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var on = false;
                for (var k = 0; sw.ElapsedMilliseconds < 3000; k++)
                {
                    on = !on;
                    var c = on ? (byte)255 : (byte)0;
                    await C.Engine.TestColorAsync(ip, c, c, c);
                    var wait = (k + 1) * interval - sw.ElapsedMilliseconds;
                    if (wait > 0) await Task.Delay((int)wait);
                }
                await C.Engine.TestColorAsync(ip, 0, 0, 0);
                await Task.Delay(1200);
            }
            await C.Engine.SetStaticAsync(One, "#FFFFFF", 60);
        });
        var answer = await Ui.Prompt("Hoe snel kon je strip het bijhouden?",
            "Bij welke stap knipperde je strip nog netjes mee (duidelijk wit/zwart, niets overgeslagen)? Vul het aantal wissels per seconde in: 2, 3, 4, 5, 6 of 8.", "4");
        if (answer == null) return;
        if (!int.TryParse(answer.Trim(), out var ok) || ok < 1 || ok > 10)
        {
            Ui.Toast("Vul een getal van 2 tot 8 in.", ToastKind.Warning);
            return;
        }
        C.Settings.Lighting.MinChangeMs = 1000 / ok;
        Ui.Toast($"Ingesteld: snelste wissel {C.Settings.Lighting.MinChangeMs} ms. Alle lichtshows passen zich hierop aan.");
    }

    private async void TestColor_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var ips = One;
        await Ui.Busy(sender, "Testen…", async () =>
        {
            var baseline = await C.Engine.CaptureAsync(ips);
            var test = new LightShow
            {
                Name = "Testkleur",
                Steps = new ObservableCollection<LightStep> { LightStep.Color("#9B5CFF", 1200, 100, 2) }
            };
            await C.Engine.PlayAsync(test, ips, CancellationToken.None);
            await Task.Delay(400);
            await C.Engine.RestoreAsync(baseline, ips);
        }, "Testkleur verzonden – knipperde je lamp 2× paars?");
    }
}
