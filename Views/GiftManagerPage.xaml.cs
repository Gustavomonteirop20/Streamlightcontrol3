using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StreamLightControl2.Core.Models;
using StreamLightControl2.Core.Services;

using EventTrigger = StreamLightControl2.Core.Models.EventTrigger;

namespace StreamLightControl2.Views;

public sealed class SeenGift
{
    public string Name { get; init; } = "";
    public long CoinsPerGift { get; init; }
    public int Times { get; init; }
    public bool CanLink { get; init; }
    public string CoinsText => CoinsPerGift > 0 ? $"{CoinsPerGift:N0} coins per stuk" : "coins onbekend";
    public string ActionText => CanLink ? "Koppelen" : "Gekoppeld";
}

public partial class GiftManagerPage : UserControl, IPageActivated
{
    private UiState S => App.State;
    private readonly ListCollectionView _ranges;
    private readonly ListCollectionView _named;

    public GiftManagerPage()
    {
        InitializeComponent();
        DataContext = S;
        _ranges = new ListCollectionView(S.Settings.Triggers)
        {
            Filter = o => o is EventTrigger { EventType: EventTypes.Gift } t && t.GiftNameList.Count == 0,
            IsLiveFiltering = true, IsLiveSorting = true
        };
        _ranges.LiveFilteringProperties.Add(nameof(EventTrigger.GiftNames));
        _ranges.LiveFilteringProperties.Add(nameof(EventTrigger.EventType));
        _ranges.SortDescriptions.Add(new SortDescription(nameof(EventTrigger.MinCoins), ListSortDirection.Ascending));
        _named = new ListCollectionView(S.Settings.Triggers)
        {
            Filter = o => o is EventTrigger { EventType: EventTypes.Gift } t && t.GiftNameList.Count > 0,
            IsLiveFiltering = true
        };
        _named.LiveFilteringProperties.Add(nameof(EventTrigger.GiftNames));
        _named.LiveFilteringProperties.Add(nameof(EventTrigger.EventType));
        RangeList.ItemsSource = _ranges;
        NamedList.ItemsSource = _named;

        S.Settings.Triggers.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (EventTrigger t in e.NewItems) t.PropertyChanged += TriggerChanged;
            Refresh();
        };
        foreach (var t in S.Settings.Triggers) t.PropertyChanged += TriggerChanged;
        S.Settings.Shows.CollectionChanged += (_, _) => Refresh();
        S.History.CollectionChanged += (_, _) => { if (IsVisible) RefreshSeen(); };
        Refresh();
    }

    public void OnActivated() { Refresh(); RefreshSeen(); }

    private void TriggerChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        NoRangesText.Visibility = _ranges.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoNamedText.Visibility = _named.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var warnings = RuleEngine.ValidateGiftRules(S.Settings);
        WarningsList.ItemsSource = warnings;
        WarningsBox.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OkBox.Visibility = warnings.Count == 0 && _ranges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTester();
    }

    private void RefreshSeen()
    {
        var linked = new HashSet<string>(S.Settings.Triggers.Where(t => t.EventType == EventTypes.Gift).SelectMany(t => t.GiftNameList), StringComparer.OrdinalIgnoreCase);
        var seen = S.History.Where(h => h.Type == EventTypes.Gift && !h.IsTest && !string.IsNullOrWhiteSpace(h.GiftName))
            .GroupBy(h => h.GiftName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SeenGift
            {
                Name = g.First().GiftName,
                Times = g.Count(),
                CoinsPerGift = g.Where(x => x.Coins > 0 && x.GiftCount > 0).Select(x => x.Coins!.Value / Math.Max(1, x.GiftCount)).DefaultIfEmpty(0).Max(),
                CanLink = !linked.Contains(g.Key)
            })
            .OrderByDescending(g => g.CoinsPerGift).ThenByDescending(g => g.Times)
            .Take(30).ToList();
        SeenList.ItemsSource = seen;
        NoSeenText.Visibility = seen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddRange_Click(object sender, RoutedEventArgs e)
    {
        var top = S.Settings.Triggers.Where(t => t.EventType == EventTypes.Gift && t.GiftNameList.Count == 0)
            .Select(t => t.MaxCoins ?? t.MinCoins ?? 0).DefaultIfEmpty(0).Max();
        var from = top + 1;
        S.Settings.Triggers.Add(new EventTrigger
        {
            Name = $"Gift {from}+ coins", EventType = EventTypes.Gift, MinCoins = from,
            ShowId = S.Settings.Shows.FirstOrDefault(s => s.PresetKey == Presets.BigGift)?.Id ?? S.Settings.Shows.FirstOrDefault()?.Id ?? "",
            Priority = Priorities.High
        });
        Ui.Toast("Bereik toegevoegd – pas de grenzen en de lichtshow aan");
    }

    private void AddNamed_Click(object sender, RoutedEventArgs e) => AddNamed("Rose");

    private void AddNamed(string name)
    {
        S.Settings.Triggers.Add(new EventTrigger
        {
            Name = "Gift: " + name, EventType = EventTypes.Gift, GiftNames = name,
            ShowId = S.Settings.Shows.FirstOrDefault(s => s.PresetKey == Presets.SmallGift)?.Id ?? S.Settings.Shows.FirstOrDefault()?.Id ?? "",
            Priority = Priorities.High
        });
        RefreshSeen();
        Ui.Toast($"Eigen effect voor ‘{name}’ toegevoegd");
    }

    private void LinkSeen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is string name) AddNamed(name);
    }

    private void Test_Click(object sender, RoutedEventArgs e) => EventManagerPage.Test((sender as Button)?.CommandParameter as EventTrigger);

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not EventTrigger t) return;
        if (!await Ui.Confirm("Regel verwijderen?", $"‘{t.Name}’ ({t.ConditionSummary}) wordt verwijderd.", "Verwijderen", danger: true)) return;
        S.Settings.Triggers.Remove(t);
        Ui.Toast("Regel verwijderd");
    }

    // ------------------------------------------------------------ tester

    private void Tester_Changed(object sender, TextChangedEventArgs e) => UpdateTester();

    private StreamEvent TesterEvent()
    {
        long.TryParse(TestCoins?.Text.Trim(), out var coins);
        return StreamEvent.Test(EventTypes.Gift, TestName?.Text.Trim() ?? "", Math.Max(0, coins));
    }

    private void UpdateTester()
    {
        if (TesterResult == null) return;
        var ev = TesterEvent();
        var trigger = RuleEngine.SelectTrigger(ev, S.Settings.Triggers);
        if (trigger == null)
        {
            TesterResult.Text = "Geen effect";
            TesterDetail.Text = "Er is geen actieve regel voor deze gift.";
            return;
        }
        var show = S.Settings.FindShow(trigger.ShowId);
        TesterResult.Text = show == null ? $"‘{trigger.Name}’ – maar er is geen lichtshow gekozen" : $"→ {show.Name}";
        TesterDetail.Text = $"Via ‘{trigger.Name}’ ({trigger.ConditionSummary}, prioriteit {trigger.PriorityLabel.ToLowerInvariant()}).";
    }

    private void SimulateGift_Click(object sender, RoutedEventArgs e)
    {
        var entry = App.Controller.Simulate(TesterEvent());
        if (entry == null) return;
        Ui.Toast($"Gift gesimuleerd: {entry.StatusLabel}" + (string.IsNullOrEmpty(entry.ShowName) ? "" : $" → {entry.ShowName}"),
            entry.Status is ProcessingStatus.Queued or ProcessingStatus.Playing ? ToastKind.Success : ToastKind.Info);
    }
}
