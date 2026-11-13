using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using StreamLightControl2.Core;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Views;

public sealed class StatTile : ObservableObject
{
    private string _value = "0";
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Value { get => _value; set => Set(ref _value, value); }
}

public partial class DashboardPage : UserControl, IPageActivated
{
    private static readonly Dictionary<string, (string label, string icon)> StatInfo = new()
    {
        ["follows"] = ("Follows", ""),
        ["gifts"] = ("Gifts", ""),
        ["coins"] = ("Coins", ""),
        ["shows"] = ("Lichtshows afgespeeld", ""),
        ["likes"] = ("Likes", ""),
        ["shares"] = ("Shares", ""),
        ["comments"] = ("Comments", ""),
        ["members"] = ("Kijkers binnengekomen", "")
    };
    private readonly ObservableCollection<StatTile> _tiles = new();
    private UiState S => App.State;

    public DashboardPage()
    {
        InitializeComponent();
        DataContext = S;
        StatsGrid.ItemsSource = _tiles;
        BuildStatChecks();
        BuildTiles();
        S.Stats.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTiles);
        S.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(UiState.IsRunning) or nameof(UiState.SafeMode)) UpdateStartStop(); };
        UpdateStartStop();
        OnActivated();
    }

    public void OnActivated()
    {
        var h = DateTime.Now.Hour;
        Greeting.Text = h < 6 ? "Goedenacht" : h < 12 ? "Goedemorgen" : h < 18 ? "Goedemiddag" : "Goedenavond";
        SinceRun.Text = "· sinds " + S.Stats.Since.ToString("HH:mm");
        UpdateTiles();
    }

    private void BuildStatChecks()
    {
        StatsChecks.Children.Clear();
        foreach (var key in DashboardOptions.AllStats)
        {
            var cb = new CheckBox
            {
                Content = StatInfo[key].label, Margin = new Thickness(0, 4, 20, 4),
                IsChecked = S.Settings.Dashboard.VisibleStats.Contains(key)
            };
            cb.Checked += (_, _) => ToggleStat(key, true);
            cb.Unchecked += (_, _) => ToggleStat(key, false);
            StatsChecks.Children.Add(cb);
        }
    }

    private void ToggleStat(string key, bool visible)
    {
        var list = S.Settings.Dashboard.VisibleStats;
        if (visible && !list.Contains(key)) list.Add(key);
        if (!visible) list.Remove(key);
        // vaste volgorde aanhouden
        S.Settings.Dashboard.VisibleStats = DashboardOptions.AllStats.Where(list.Contains).ToList();
        App.Controller.Store.RequestSave();
        BuildTiles();
    }

    private void BuildTiles()
    {
        _tiles.Clear();
        foreach (var key in S.Settings.Dashboard.VisibleStats)
            if (StatInfo.TryGetValue(key, out var info))
                _tiles.Add(new StatTile { Key = key, Label = info.label, Icon = info.icon });
        UpdateTiles();
    }

    private void UpdateTiles()
    {
        var st = S.Stats;
        foreach (var t in _tiles)
            t.Value = t.Key switch
            {
                "follows" => st.Follows.ToString("N0"),
                "gifts" => st.Gifts.ToString("N0"),
                "coins" => st.Coins.ToString("N0"),
                "shows" => st.ShowsPlayed.ToString("N0"),
                "likes" => st.Likes.ToString("N0"),
                "shares" => st.Shares.ToString("N0"),
                "comments" => st.Comments.ToString("N0"),
                "members" => st.Members.ToString("N0"),
                _ => "0"
            };
    }

    private void UpdateStartStop()
    {
        if (S.IsRunning)
        {
            StartStopButton.Content = "Stop automatisering";
            StartStopButton.Tag = "";
            StartStopButton.Style = (Style)FindResource("SecondaryButton");
        }
        else
        {
            StartStopButton.Content = "Start automatisering";
            StartStopButton.Tag = "";
            StartStopButton.Style = (Style)FindResource("PrimaryButton");
        }
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (S.IsRunning)
        {
            if (S.IsPlaying || S.QueueCount > 0)
            {
                if (!await Ui.Confirm("Automatisering stoppen?", "Het lopende effect stopt en de wachtrij wordt leeggemaakt. Nieuwe events worden wel nog gelogd.", "Stoppen"))
                    return;
            }
            App.Controller.StopAutomation();
            Ui.Toast("Automatisering gestopt", ToastKind.Info);
        }
        else
        {
            await Ui.Busy(sender, "Starten…", App.Controller.StartAutomationAsync);
            UpdateStartStop();
            if (S.SafeMode) Ui.Toast("Let op: Safe Mode staat nog aan, er wordt niets afgespeeld.", ToastKind.Warning);
            else Ui.Toast(S.BetterTokConnected ? "Automatisering gestart – je bent LIVE" : "Automatisering gestart, wacht op BetterTok", S.BetterTokConnected ? ToastKind.Success : ToastKind.Warning);
        }
    }

    private void CustomizeStats_Click(object sender, RoutedEventArgs e) =>
        StatsCustomizer.Visibility = StatsCustomizer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void Quick_Click(object sender, RoutedEventArgs e) => QuickActions.Run(sender, (string)((Button)sender).CommandParameter);
    private void Skip_Click(object sender, RoutedEventArgs e) => App.Controller.Queue.SkipCurrent();
    private void StopAll_Click(object sender, RoutedEventArgs e) => QuickActions.Run(sender, "stop");
    private void GoHistory_Click(object sender, RoutedEventArgs e) => Ui.Navigate("history");
    private void GoQueue_Click(object sender, RoutedEventArgs e) => Ui.Navigate("queue");
    private void GoBetterTok_Click(object sender, RoutedEventArgs e) => Ui.Navigate("bettertok");
    private void GoDevices_Click(object sender, RoutedEventArgs e) => Ui.Navigate("devices");
}
