using System.Windows;
using System.Windows.Controls;
using StreamLightControl2.Core.Models;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2.Views;

public partial class BetterTokPage : UserControl, IPageActivated
{
    private UiState S => App.State;
    private AutomationController C => App.Controller;

    public BetterTokPage()
    {
        InitializeComponent();
        DataContext = S;
        S.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(UiState.BetterTokText) or nameof(UiState.ErrorCount)) UpdateStatus(); };
        S.Errors.CollectionChanged += (_, _) => UpdateStatus();
        S.Settings.BetterTok.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BetterTokOptions.Mode)) Dispatcher.BeginInvoke(UpdateMode); };
        UpdateMode();
        UpdateKeyStatus();
        UpdateStatus();
        C.DonateWave.StateChanged += () => Dispatcher.BeginInvoke(UpdateDonateWave);
        RefreshShoutouts();
        S.Errors.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateDonateWave);
        UpdateDonateWave();
        UpdateOverlays();
    }

    public void OnActivated() { UpdateStatus(); UpdateKeyStatus(); UpdateDonateWave(); RefreshShoutouts(); UpdateOverlays(); }

    // ------------------------------------------------------------------ Kerst-overlays

    private sealed record OverlayLink(string Name, string Url, string Size, string Preview);

    private void UpdateOverlays()
    {
        var ov = C.Overlays;
        OvDot.Fill = Ui.Res(!S.Settings.Overlays.Enabled ? "Text3Brush" : ov.IsRunning ? "SuccessBrush" : "DangerBrush");
        OvStatus.Text = !S.Settings.Overlays.Enabled ? "Uit" : ov.IsRunning ? $"Aan · {C.Donations.Count} tips bijgehouden" : ov.Status;
        var port = S.Settings.Overlays.Port;
        OvLinks.ItemsSource = OverlayServer.Views.Select(v => new OverlayLink(v.Name, $"http://localhost:{port}/kerst/{v.Path}", v.Size,
            $"http://localhost:{port}/kerst/{v.Path}?bg=1")).ToList();
    }

    private void OvSwitch_Click(object sender, RoutedEventArgs e) { C.Overlays.Apply(); UpdateOverlays(); }

    private void OvPort_LostFocus(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(() => { C.Overlays.Apply(); UpdateOverlays(); });

    private void OvCopy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string url) return;
        try { Clipboard.SetText(url); Ui.Toast("Adres gekopieerd"); }
        catch (Exception ex) { Ui.Toast("Kopiëren mislukt: " + ex.Message, ToastKind.Error); }
    }

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Ui.Toast("Openen mislukt: " + ex.Message, ToastKind.Error); }
    }

    private void OvOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url) Open(url);
    }

    private void OvIndex_Click(object sender, RoutedEventArgs e) => Open($"http://localhost:{S.Settings.Overlays.Port}/");

    private void OvResetGoal_Click(object sender, RoutedEventArgs e)
    {
        C.ResetTipGoal();
        Ui.Toast("Het doel telt opnieuw vanaf € 0");
    }

    private void OvClear_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(Window.GetWindow(this), "Alle bijgehouden tips wissen? Ranking, weekkampioen, vandaag, recente tips en het doel beginnen dan opnieuw. Dit kan niet ongedaan worden.",
            "Tips wissen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        C.Donations.Clear();
        UpdateOverlays();
        Ui.Toast("Tips gewist");
    }

    private void RefreshShoutouts() =>
        ShoutoutList.ItemsSource = S.Settings.Triggers.Where(t => t.EventType == EventTypes.Tip && t.TipKind == TipKinds.Shoutout && t.Id.StartsWith("tw-special-")).ToList();

    private static void Nudge(object sender, int ms)
    {
        if ((sender as FrameworkElement)?.Tag is StreamLightControl2.Core.Models.EventTrigger t) t.StartDelayMs += ms;
    }
    private void DelayMinus_Click(object sender, RoutedEventArgs e) => Nudge(sender, -100);
    private void DelayPlus_Click(object sender, RoutedEventArgs e) => Nudge(sender, +100);

    private void DelayReset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (id, _, _, animMs, _, _) in Presets.Specials)
        {
            var t = S.Settings.Triggers.FirstOrDefault(x => x.Id == "tw-special-" + id);
            if (t == null) continue;
            t.StartDelayMs = animMs;
        }
        Ui.Toast("Gemeten animatietijden teruggezet");
    }

    // ------------------------------------------------------------------ DonateWave

    private void UpdateDonateWave()
    {
        var dw = C.DonateWave;
        var on = S.Settings.DonateWave.Enabled;
        DwDot.Fill = Ui.Res(!on ? "Text3Brush" : dw.State switch
        {
            ConnectionState.Connected => "SuccessBrush",
            ConnectionState.Failed => "DangerBrush",
            _ => "WarningBrush"
        });
        DwStatus.Text = !on ? "Uit" : dw.StatusText + (dw.LastPollAt is { } lp ? $" · laatste controle {lp:HH:mm:ss}" : "")
            + (dw.TipsReceived > 0 ? $" · {dw.TipsReceived} tip(s) deze sessie" : "")
            + (dw.LastBatchText.Length > 0 ? "\nLaatst binnen: " + dw.LastBatchText : "")
            + (dw.LastRequest is { } lr ? $"\nAPI: GET {dw.ApiUrl} → {lr.StatusLabel} ({lr.Ms} ms)" + (lr.Status is >= 200 and < 300 ? "" : " · " + lr.Info) : "")
            + "\nAlle details: Error Center › Verbindingsdetails";
        var key = C.GetDonateWaveKey();
        DwKeyStatus.Text = string.IsNullOrEmpty(key) ? "Nog geen sleutel opgeslagen." : "Opgeslagen: " + SecretProtector.Mask(key);
        DwRemoveButton.Visibility = string.IsNullOrEmpty(key) ? Visibility.Collapsed : Visibility.Visible;
        var problem = S.Errors.FirstOrDefault(x => x.Category == ErrorCategory.DonateWave);
        DwProblemBox.Visibility = problem == null || !on || dw.State == ConnectionState.Connected ? Visibility.Collapsed : Visibility.Visible;
        if (problem == null) return;
        DwProblemBox.Background = Ui.Res(problem.IsWarning ? "WarningSoftBrush" : "DangerSoftBrush");
        DwProblemTitle.Foreground = Ui.Res(problem.IsWarning ? "WarningBrush" : "DangerBrush");
        DwProblemTitle.Text = "Waarom lukt het niet? " + problem.Title;
        DwProblemText.Text = problem.Explanation;
        DwProblemSolution.Text = "Oplossing: " + problem.Solution;
    }

    private void DwSave_Click(object sender, RoutedEventArgs e)
    {
        var key = DwKeyBox.Password.Trim();
        if (key.Length > 0)
        {
            if (!key.StartsWith("dw_evt_")) Ui.Toast("Let op: DonateWave-sleutels beginnen met dw_evt_.", ToastKind.Warning);
            C.SetDonateWaveKey(key);
            DwKeyBox.Clear();
        }
        else if (string.IsNullOrEmpty(C.GetDonateWaveKey())) { Ui.Toast("Plak eerst je DonateWave-sleutel.", ToastKind.Warning); return; }
        S.Settings.DonateWave.Enabled = true;
        C.ApplyDonateWave();
        UpdateDonateWave();
        Ui.Toast("DonateWave gekoppeld – tips spelen nu een lichtshow");
    }

    private void DwSwitch_Click(object sender, RoutedEventArgs e)
    {
        C.ApplyDonateWave();
        UpdateDonateWave();
    }

    private async void DwRemove_Click(object sender, RoutedEventArgs e)
    {
        if (!await Ui.Confirm("DonateWave-sleutel verwijderen?", "Tips geven daarna geen lichtshow meer tot je een nieuwe sleutel invult. Trek de koppeling ook in bij DonateWave › Koppelingen.", "Verwijderen", danger: true)) return;
        S.Settings.DonateWave.Enabled = false;
        C.SetDonateWaveKey("");
        C.ApplyDonateWave();
        UpdateDonateWave();
        Ui.Toast("DonateWave-sleutel verwijderd");
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (LocalPanel == null) return;
        Dispatcher.BeginInvoke(UpdateMode);
    }

    private void UpdateMode()
    {
        var cloud = S.Settings.BetterTok.Mode == BetterTokMode.Cloud;
        LocalPanel.Visibility = cloud ? Visibility.Collapsed : Visibility.Visible;
        CloudPanel.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateKeyStatus()
    {
        var key = C.GetApiKey();
        KeyStatus.Text = string.IsNullOrEmpty(key) ? "Nog geen key opgeslagen." : "Opgeslagen: " + SecretProtector.Mask(key);
        RemoveKeyButton.Visibility = string.IsNullOrEmpty(key) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateStatus()
    {
        ConnectedUrl.Text = C.BetterTok.ConnectedUrl ?? "";
        ConnectButton.Content = S.BetterTokConnected ? "Opnieuw verbinden" : "Verbinden";
        var problem = S.Errors.FirstOrDefault(x => x.Category == ErrorCategory.BetterTok);
        ProblemBox.Visibility = problem == null || S.BetterTokConnected ? Visibility.Collapsed : Visibility.Visible;
        if (problem == null) return;
        ProblemBox.Background = Ui.Res(problem.IsWarning ? "WarningSoftBrush" : "DangerSoftBrush");
        ProblemTitle.Foreground = Ui.Res(problem.IsWarning ? "WarningBrush" : "DangerBrush");
        ProblemTitle.Text = "Waarom lukt het niet? " + problem.Title;
        ProblemText.Text = problem.Explanation;
        ProblemSolution.Text = problem.Solution;
        ProblemDetail.Text = problem.Detail;
        ProblemDetail.Visibility = string.IsNullOrEmpty(problem.Detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        // eventueel getypte maar niet opgeslagen key meenemen
        if (S.Settings.BetterTok.Mode == BetterTokMode.Cloud && KeyBox.Password.Length > 0) SaveKey();
        await Ui.Busy(sender, "Verbinden…", async () =>
        {
            var ok = await C.ConnectBetterTokAsync();
            UpdateStatus();
            if (ok) Ui.Toast("Verbonden met BetterTok");
            else Ui.Toast("Verbinden mislukt – zie de uitleg hieronder", ToastKind.Warning);
        });
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await C.BetterTok.DisconnectAsync();
        UpdateStatus();
        Ui.Toast("Verbinding verbroken", ToastKind.Info);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        await Ui.Busy(sender, "Testen…", async () =>
        {
            var (ok, message) = await C.BetterTok.TestAsync();
            TestResult.Visibility = Visibility.Visible;
            TestResult.Foreground = Ui.Res(ok ? "SuccessBrush" : "WarningBrush");
            TestResult.Text = (ok ? "✓ " : "") + message;
        });
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (KeyBox.Password.Trim().Length == 0) { Ui.Toast("Plak eerst je API-key.", ToastKind.Warning); return; }
        SaveKey();
    }

    private void SaveKey()
    {
        var key = KeyBox.Password.Trim();
        if (!key.StartsWith("btk_live_")) Ui.Toast("Let op: BetterTok-keys beginnen normaal met btk_live_.", ToastKind.Warning);
        C.SetApiKey(key);
        KeyBox.Clear();
        UpdateKeyStatus();
        Ui.Toast("API-key versleuteld opgeslagen");
    }

    private async void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (!await Ui.Confirm("API-key verwijderen?", "Cloud Mode werkt daarna pas weer als je een nieuwe key invult.", "Verwijderen", danger: true)) return;
        C.SetApiKey("");
        UpdateKeyStatus();
        Ui.Toast("API-key verwijderd");
    }

    private void CopyRaw_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, S.RawLog));
            Ui.Toast("Berichten gekopieerd");
        }
        catch (Exception ex) { Ui.Toast("Kopiëren mislukt: " + ex.Message, ToastKind.Error); }
    }

    private void ClearRaw_Click(object sender, RoutedEventArgs e) => S.RawLog.Clear();
}
