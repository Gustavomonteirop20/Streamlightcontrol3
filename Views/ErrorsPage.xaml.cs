using System.Windows;
using System.Windows.Controls;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Views;

public partial class ErrorsPage : UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ErrorsPage()
    {
        InitializeComponent();
        DataContext = App.State;
        _timer.Tick += (_, _) => RefreshReport();
        Loaded += (_, _) => { RefreshReport(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void RefreshReport()
    {
        try { ReportText.Text = App.Controller.ConnectionReport(); }
        catch (Exception ex) { ReportText.Text = "Details konden niet worden gemaakt: " + ex.Message; }
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(App.Controller.ConnectionReport()); Ui.Toast("Verbindingsdetails gekopieerd"); }
        catch (Exception ex) { Ui.Toast("Kopiëren mislukt: " + ex.Message, ToastKind.Error); }
    }

    private void RetryDonateWave_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Controller.Settings.DonateWave.Enabled) { Ui.Toast("DonateWave staat uit – zet hem aan bij Verbinding.", ToastKind.Warning); return; }
        App.Controller.ApplyDonateWave();
        RefreshReport();
        Ui.Toast("DonateWave: opnieuw verbonden");
    }

    private static ErrorEntry? Of(object sender) => (sender as Button)?.CommandParameter as ErrorEntry;

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        var err = Of(sender);
        if (err == null) return;
        Ui.Navigate(err.Key switch
        {
            "ev.noshow" => "events",
            "q.full" => "settings",
            "q.play" => "shows",
            "app.save" or "app.load" or "app.crash" => "settings",
            _ => err.Category switch
            {
                ErrorCategory.BetterTok => "bettertok",
                ErrorCategory.Govee => "devices",
                ErrorCategory.Queue => "queue",
                ErrorCategory.Event => "bettertok",
                ErrorCategory.Spotify => "spotify",
                ErrorCategory.DonateWave => "bettertok",
                _ => "settings"
            }
        });
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        var err = Of(sender);
        if (err != null) App.Controller.Errors.Resolve(err.Key);
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        App.Controller.Errors.Clear();
        Ui.Toast("Meldingen gewist");
    }
}
