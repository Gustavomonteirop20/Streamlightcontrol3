using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StreamLightControl2.Core.Models;

using EventTrigger = StreamLightControl2.Core.Models.EventTrigger;

namespace StreamLightControl2.Views;

public partial class EventManagerPage : UserControl
{
    private UiState S => App.State;
    private readonly ListCollectionView _view;
    private string _filter = "";

    public EventManagerPage()
    {
        InitializeComponent();
        DataContext = S;
        _view = new ListCollectionView(S.Settings.Triggers);
        _view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(EventTrigger.EventType), System.ComponentModel.ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(EventTrigger.MinCoins), System.ComponentModel.ListSortDirection.Ascending));
        TriggerList.ItemsSource = _view;
        DetailShow.ItemsSource = S.Settings.Shows;
        ApplyFilter();
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) _filter = tag;
        if (TriggerList != null) ApplyFilter();
    }

    private void ApplyFilter()
    {
        _view.Filter = _filter switch
        {
            "" => null,
            "other" => o => o is EventTrigger t && t.EventType is EventTypes.Member or EventTypes.Subscribe,
            _ => o => o is EventTrigger t && t.EventType == _filter
        };
        EmptyText.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var type = _filter is "" or "other" ? EventTypes.Follow : _filter;
        var t = new EventTrigger
        {
            Name = "Nieuwe " + EventTypes.Label(type).ToLowerInvariant() + "-koppeling",
            EventType = type,
            ShowId = S.Settings.Shows.FirstOrDefault()?.Id ?? "",
            Priority = Priorities.Normal
        };
        S.Settings.Triggers.Add(t);
        ApplyFilter();
        TriggerList.SelectedItem = t;
        TriggerList.ScrollIntoView(t);
        Ui.Toast("Koppeling toegevoegd – kies een lichtshow en pas de details aan");
    }

    private void TriggerList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails(TriggerList.SelectedItem as EventTrigger);

    private void ShowDetails(EventTrigger? t)
    {
        Details.DataContext = t;
        Details.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        NoSelection.Visibility = t == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateSections(t);
    }

    private void DetailType_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSections(Details.DataContext as EventTrigger);

    private void UpdateSections(EventTrigger? t)
    {
        GiftSection.Visibility = t?.EventType == EventTypes.Gift ? Visibility.Visible : Visibility.Collapsed;
        CommentSection.Visibility = t?.EventType == EventTypes.Comment ? Visibility.Visible : Visibility.Collapsed;
        MemberSection.Visibility = t?.EventType == EventTypes.Member ? Visibility.Visible : Visibility.Collapsed;
        LikeSection.Visibility = t?.EventType == EventTypes.Like ? Visibility.Visible : Visibility.Collapsed;
        TipSection.Visibility = t?.EventType == EventTypes.Tip ? Visibility.Visible : Visibility.Collapsed;
    }

    private static EventTrigger? Of(object sender) => (sender as Button)?.CommandParameter as EventTrigger;

    private void Test_Click(object sender, RoutedEventArgs e) => Test(Of(sender));
    private void DetailTest_Click(object sender, RoutedEventArgs e) => Test(Details.DataContext as EventTrigger);

    public static void Test(EventTrigger? t)
    {
        if (t == null) return;
        if (App.Controller.Settings.FindShow(t.ShowId) == null) { Ui.Toast("Kies eerst een lichtshow voor deze koppeling.", ToastKind.Warning); return; }
        if (App.Controller.Targets.Count == 0) { Ui.Toast("Geen actieve lamp. Voeg je lamp toe in Device Manager.", ToastKind.Warning); return; }
        App.Controller.TestTrigger(t);
        Ui.Toast($"Test: ‘{t.Name}’ wordt afgespeeld", ToastKind.Info);
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => TriggerList.SelectedItem = Of(sender);
    private async void Delete_Click(object sender, RoutedEventArgs e) => await Delete(Of(sender));
    private async void DetailDelete_Click(object sender, RoutedEventArgs e) => await Delete(Details.DataContext as EventTrigger);

    private async Task Delete(EventTrigger? t)
    {
        if (t == null) return;
        if (!await Ui.Confirm("Koppeling verwijderen?", $"‘{t.Name}’ wordt verwijderd. De lichtshow zelf blijft bestaan.", "Verwijderen", danger: true)) return;
        S.Settings.Triggers.Remove(t);
        ShowDetails(null);
        ApplyFilter();
        Ui.Toast("Koppeling verwijderd");
    }

    private void Simulate_Click(object sender, RoutedEventArgs e)
    {
        var type = (string)((Button)sender).CommandParameter;
        long.TryParse(SimCoins.Text.Trim(), out var coins);
        var ev = type switch
        {
            EventTypes.Gift => StreamEvent.Test(EventTypes.Gift, string.IsNullOrWhiteSpace(SimGiftName.Text) ? "Testgift" : SimGiftName.Text.Trim(), Math.Max(1, coins)),
            EventTypes.Like => StreamEvent.Test(EventTypes.Like, likes: 1),
            EventTypes.Comment => StreamEvent.Test(EventTypes.Comment, comment: "test"),
            EventTypes.Tip => StreamEvent.Test(EventTypes.Tip, EventTrigger.Euro(500), 500),
            "shoutout" => new StreamEvent { Type = EventTypes.Tip, RawType = "tip.received", DisplayName = "Test · shout-out", GiftName = EventTrigger.Euro(500) + " · shout-out",
                                            TotalCoins = 500, CoinsPerItem = 500, TipKind = TipKinds.Shoutout, IsTest = true, DedupeKey = "test:" + Guid.NewGuid().ToString("N") },
            _ when type.StartsWith("member:") => new StreamEvent { Type = EventTypes.Member, RawType = "member", User = type[7..], DisplayName = type[7..],
                                                                   IsTest = true, DedupeKey = "test:" + Guid.NewGuid().ToString("N") },
            _ => StreamEvent.Test(type)
        };
        var entry = App.Controller.Simulate(ev);
        SimResult.Visibility = Visibility.Visible;
        SimResult.Text = entry == null
            ? "Niet verwerkt."
            : $"{(type == "shoutout" ? "Shout-out" : type.StartsWith("member:") ? "Binnenkomst " + type[7..] : EventTypes.Label(type))}: {entry.StatusLabel}" + (string.IsNullOrEmpty(entry.ShowName) ? "" : $" → ‘{entry.ShowName}’") + $"  ({entry.Detail})";
        if (App.Controller.Targets.Count == 0 && entry?.Status == ProcessingStatus.Queued)
            SimResult.Text += "  Let op: er is geen actieve lamp.";
    }
}
