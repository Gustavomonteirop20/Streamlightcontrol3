using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using StreamLightControl2.Views;

namespace StreamLightControl2;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly Dictionary<string, (string title, string subtitle)> _titles = new()
    {
        ["dashboard"] = ("Dashboard", "Overzicht van je stream en je verlichting"),
        ["shows"] = ("Light Shows", "Maak, test en bewaar je eigen lichteffecten"),
        ["events"] = ("Event Manager", "Welk TikTok-event speelt welke lichtshow"),
        ["gifts"] = ("Gift Manager", "Regels voor gifts en coin-bedragen"),
        ["follows"] = ("Follow Manager", "Wat er gebeurt als iemand je volgt"),
        ["spotify"] = ("Spotify", "Je lampen bewegen mee met de muziek die je afspeelt"),
        ["devices"] = ("Device Manager", "Je Govee-lampen zoeken, testen en bedienen"),
        ["history"] = ("Event History", "Alle ontvangen events en wat ermee is gedaan"),
        ["settings"] = ("Instellingen", "Algemeen, verlichting, wachtrij en back-ups"),
        ["queue"] = ("Wachtrij", "Wat er nu speelt en wat er nog komt"),
        ["bettertok"] = ("BetterTok", "Verbinding met je TikTok LIVE-events"),
        ["errors"] = ("Error Center", "Problemen, met uitleg en oplossing")
    };
    private TaskCompletionSource<bool>? _dialog;
    private Rect _normalBounds;
    private bool _streamMode;

    public MainWindow()
    {
        InitializeComponent();
        Ui.Window = this;
        DataContext = App.State;
        Navigate("dashboard");
        App.State.SavedPulse += PulseSaved;
        App.State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UiState.SafeMode)) UpdateSafeModeButton();
            if (e.PropertyName is nameof(UiState.IsLive) or nameof(UiState.LiveText)) UpdateLivePulse();
            if (e.PropertyName is nameof(UiState.IsSpotifyMode)) UpdateModeSwitch();
        };
        UpdateModeSwitch();
        UpdateSafeModeButton();
        UpdateLivePulse();
        SourceInitialized += (_, _) => UseDarkTitleBar();
        App.Updater.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Updater.HasUpdate)) UpdateBannerState(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && DialogHost.Visibility == Visibility.Visible) CloseDialog(false); };
    }

    // ------------------------------------------------------------ navigatie

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { CommandParameter: string key }) ShowPage(key);
    }

    public void Navigate(string key)
    {
        foreach (var rb in NavButtons())
            if (rb.CommandParameter as string == key) { if (rb.IsChecked == true) ShowPage(key); else rb.IsChecked = true; return; }
        ShowPage(key);
    }

    private IEnumerable<RadioButton> NavButtons()
    {
        foreach (var child in NavPanel.Children)
        {
            if (child is RadioButton rb) yield return rb;
            else if (child is Panel p) foreach (var r in p.Children.OfType<RadioButton>()) yield return r;
        }
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "dashboard" => new DashboardPage(),
                "shows" => new LightShowsPage(),
                "events" => new EventManagerPage(),
                "gifts" => new GiftManagerPage(),
                "follows" => new FollowManagerPage(),
                "spotify" => new SpotifyPage(),
                "devices" => new DevicesPage(),
                "history" => new HistoryPage(),
                "settings" => new SettingsPage(),
                "queue" => new QueuePage(),
                "bettertok" => new BetterTokPage(),
                "errors" => new ErrorsPage(),
                _ => new DashboardPage()
            };
            _pages[key] = page;
        }
        if (_titles.TryGetValue(key, out var t)) { PageTitle.Text = t.title; PageSubtitle.Text = t.subtitle; }
        if (ReferenceEquals(PageHost.Content, page)) return;
        PageHost.Content = page;
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });
        if (page is IPageActivated activated) activated.OnActivated();
    }

    private void GoBetterTok_Click(object sender, RoutedEventArgs e) => Navigate("bettertok");
    private void GoSpotify_Click(object sender, RoutedEventArgs e) => Navigate("spotify");

    // ------------------------------------------------------------ modus: Stream ↔ Spotify

    private void UpdateModeSwitch()
    {
        ModeStream.IsChecked = !App.State.IsSpotifyMode;
        ModeSpotify.IsChecked = App.State.IsSpotifyMode;
    }

    private async void ModeStream_Click(object sender, RoutedEventArgs e) => await SwitchModeAsync(Core.Models.AppMode.Stream);
    private async void ModeSpotify_Click(object sender, RoutedEventArgs e) => await SwitchModeAsync(Core.Models.AppMode.Spotify);

    public async Task SwitchModeAsync(Core.Models.AppMode mode)
    {
        var c = App.Controller;
        if (c.Mode == mode) { UpdateModeSwitch(); return; }
        if (mode == Core.Models.AppMode.Spotify && c.Queue.Current != null &&
            !await ShowConfirm("Naar Spotify-modus?", "Er speelt nu een lichtshow van je stream. In Spotify-modus stopt BetterTok en reageren je lampen op de muziek.", "Overschakelen", false))
        { UpdateModeSwitch(); return; }
        try
        {
            await c.SetModeAsync(mode);
            Ui.Toast(mode == Core.Models.AppMode.Spotify
                ? "Spotify-modus: je lampen bewegen mee met de muziek. BetterTok staat uit."
                : "Stream-modus: BetterTok stuurt je lampen weer.", ToastKind.Success);
            if (mode == Core.Models.AppMode.Spotify) Navigate("spotify");
        }
        catch (Exception ex) { Ui.Toast("Overschakelen mislukt: " + ex.Message, ToastKind.Error); }
        UpdateModeSwitch();
    }
    private void GoDevices_Click(object sender, RoutedEventArgs e) => Navigate("devices");
    private void Brightness_Click(object sender, RoutedEventArgs e) => BrightnessPopup.IsOpen = !BrightnessPopup.IsOpen;

    private void BrightnessPreset_Click(object sender, RoutedEventArgs e)
    {
        // Tag staat in de icoonletter van de knopstijl; daarom uit Content lezen
        if (sender is Button { Content: string text } && int.TryParse(text.TrimEnd('%'), out var v))
            App.Controller.Settings.Lighting.ShowBrightness = v;
    }

    private void GoSettings_Click(object sender, RoutedEventArgs e) => Navigate("settings");

    // ------------------------------------------------------------ updates

    private bool _bannerDismissed;
    private void UpdateBannerState()
    {
        UpdateBannerText.Text = App.Updater.BannerText;
        UpdateBanner.Visibility = App.Updater.HasUpdate && !_bannerDismissed ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void UpdateNow_Click(object sender, RoutedEventArgs e) => await Ui.Busy(sender, "Bezig…", () => App.Updater.InstallAsync());
    private void UpdateLater_Click(object sender, RoutedEventArgs e) { _bannerDismissed = true; UpdateBannerState(); }

    // ------------------------------------------------------------ Safe Mode / Stream Mode

    private async void SafeMode_Click(object sender, RoutedEventArgs e) => await ToggleSafeModeAsync();

    public async Task ToggleSafeModeAsync()
    {
        var c = App.Controller;
        if (!c.SafeMode)
        {
            c.SetSafeMode(true);
            Ui.Toast("Safe Mode aan – alle automatiseringen staan uit. Je instellingen zijn bewaard.", ToastKind.Warning);
        }
        else
        {
            c.SetSafeMode(false);
            Ui.Toast("Safe Mode uit – automatisering kan weer afspelen.");
        }
        await Task.CompletedTask;
    }

    private void UpdateSafeModeButton()
    {
        var on = App.State.SafeMode;
        SafeModeButton.Content = on ? "Safe Mode aan" : "Safe Mode";
        SafeModeButton.Background = Ui.Res(on ? "WarningSoftBrush" : "ElevatedBrush");
        SafeModeButton.Foreground = Ui.Res(on ? "WarningBrush" : "TextBrush");
        SafeModeButton.BorderBrush = Ui.Res(on ? "WarningBrush" : "LineStrongBrush");
    }

    private void UpdateLivePulse()
    {
        if (App.State.IsLive)
        {
            var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            LiveDot.BeginAnimation(OpacityProperty, pulse);
        }
        else LiveDot.BeginAnimation(OpacityProperty, null);
    }

    private void StreamMode_Click(object sender, RoutedEventArgs e) => SetStreamMode(true);

    public void SetStreamMode(bool on)
    {
        if (on == _streamMode) return;
        _streamMode = on;
        if (on)
        {
            _normalBounds = new Rect(Left, Top, Width, Height);
            StreamHost.Content ??= new StreamModeView();
            FullRoot.Visibility = Visibility.Collapsed;
            StreamHost.Visibility = Visibility.Visible;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            MinWidth = 380; MinHeight = 560;
            Width = 400; Height = 700;
            Topmost = App.Controller.Settings.General.StreamModeTopmost;
            Title = "Stream Light Control – Stream Mode";
        }
        else
        {
            StreamHost.Visibility = Visibility.Collapsed;
            FullRoot.Visibility = Visibility.Visible;
            Topmost = false;
            MinWidth = 1100; MinHeight = 720;
            Left = _normalBounds.Left; Top = _normalBounds.Top; Width = _normalBounds.Width; Height = _normalBounds.Height;
            Title = "Stream Light Control";
        }
    }

    // ------------------------------------------------------------ opgeslagen-indicator en meldingen

    private DateTime _lastPulse;
    private void PulseSaved()
    {
        if ((DateTime.Now - _lastPulse).TotalMilliseconds < 400) return;
        _lastPulse = DateTime.Now;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2100))));
        SavedIndicator.BeginAnimation(OpacityProperty, anim);
    }

    public void ShowToast(string message, ToastKind kind)
    {
        var (icon, brush) = kind switch
        {
            ToastKind.Success => ("", "SuccessBrush"),
            ToastKind.Warning => ("", "WarningBrush"),
            ToastKind.Error => ("", "DangerBrush"),
            _ => ("", "AccentHoverBrush")
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, Foreground = Ui.Res(brush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) });
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var toast = new Border
        {
            Style = (Style)FindResource("Card"), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 8, 0, 0),
            Background = Ui.Res("ElevatedBrush"), BorderBrush = Ui.Res("LineStrongBrush"), Child = row, Opacity = 0,
            RenderTransform = new TranslateTransform(0, 10)
        };
        ToastHost.Children.Add(toast);
        while (ToastHost.Children.Count > 4) ToastHost.Children.RemoveAt(0);
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        ((TranslateTransform)toast.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase() });
        var stay = kind == ToastKind.Error ? 6000 : 3200;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(stay) };
        fade.Completed += (_, _) => ToastHost.Children.Remove(toast);
        toast.BeginAnimation(OpacityProperty, fade);
    }

    // ------------------------------------------------------------ dialogen

    public Task<bool> ShowConfirm(string title, string message, string confirmText, bool danger)
    {
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogInput.Visibility = Visibility.Collapsed;
        DialogOk.Content = confirmText;
        DialogOk.Style = (Style)FindResource(danger ? "DangerButton" : "PrimaryButton");
        return OpenDialog();
    }

    public async Task<string?> ShowPrompt(string title, string label, string initial)
    {
        DialogTitle.Text = title;
        DialogMessage.Text = label;
        DialogInput.Text = initial;
        DialogInput.Visibility = Visibility.Visible;
        DialogOk.Content = "Opslaan";
        DialogOk.Style = (Style)FindResource("PrimaryButton");
        var ok = await OpenDialog(focusInput: true);
        return ok ? DialogInput.Text.Trim() : null;
    }

    private Task<bool> OpenDialog(bool focusInput = false)
    {
        _dialog?.TrySetResult(false);
        _dialog = new TaskCompletionSource<bool>();
        DialogHost.Visibility = Visibility.Visible;
        DialogHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        Dispatcher.BeginInvoke(() =>
        {
            if (focusInput) { DialogInput.Focus(); DialogInput.SelectAll(); }
            else DialogOk.Focus();
        });
        return _dialog.Task;
    }

    private void CloseDialog(bool result)
    {
        DialogHost.Visibility = Visibility.Collapsed;
        _dialog?.TrySetResult(result);
        _dialog = null;
    }

    private void DialogOk_Click(object sender, RoutedEventArgs e) => CloseDialog(true);
    private void DialogCancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
    private void DialogInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) CloseDialog(true); }

    // ------------------------------------------------------------ donkere titelbalk (Windows 10 20H1+ / 11)

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));           // DWMWA_USE_IMMERSIVE_DARK_MODE
            // kleur van de zijbalk (thema) als 0x00BBGGRR
            var c = Application.Current.TryFindResource("SidebarColor") is System.Windows.Media.Color sc ? sc : System.Windows.Media.Color.FromRgb(0x0D, 0x0E, 0x13);
            var caption = c.R | (c.G << 8) | (c.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));      // DWMWA_CAPTION_COLOR (Windows 11)
        }
        catch { /* oudere Windows-versies: standaard titelbalk */ }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        App.Controller.Store.SaveNow();
        base.OnClosing(e);
    }
}

/// <summary>Pagina's die iets willen verversen als ze worden geopend.</summary>
public interface IPageActivated
{
    void OnActivated();
}
