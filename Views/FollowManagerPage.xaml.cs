using System.Windows;
using System.Windows.Controls;
using StreamLightControl2.Core.Models;
using StreamLightControl2.Core.Services;

using EventTrigger = StreamLightControl2.Core.Models.EventTrigger;

namespace StreamLightControl2.Views;

public partial class FollowManagerPage : UserControl, IPageActivated
{
    private UiState S => App.State;

    public FollowManagerPage()
    {
        InitializeComponent();
        DataContext = S;
        ShowCombo.ItemsSource = S.Settings.Shows;
        S.History.CollectionChanged += (_, _) => { if (IsVisible) RefreshFollowers(); };
        S.Settings.Triggers.CollectionChanged += (_, _) => Bind();
        Bind();
        RefreshFollowers();
    }

    public void OnActivated() { Bind(); RefreshFollowers(); }

    private EventTrigger? Primary => S.Settings.Triggers.FirstOrDefault(t => t.EventType == EventTypes.Follow);

    private void Bind()
    {
        var t = Primary;
        TriggerPanel.DataContext = t;
        TriggerOptions.DataContext = t;
        TriggerPanel.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        TriggerOptions.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        NoTrigger.Visibility = t == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshFollowers()
    {
        var list = S.History.Where(h => h.Type == EventTypes.Follow).Take(40).ToList();
        FollowerList.ItemsSource = list;
        NoFollowers.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        S.Settings.Triggers.Add(new EventTrigger
        {
            Name = "Nieuwe follower", EventType = EventTypes.Follow, Priority = Priorities.Normal, MaxInQueue = 3,
            ShowId = S.Settings.Shows.FirstOrDefault(s => s.PresetKey == Presets.Follow)?.Id ?? S.Settings.Shows.FirstOrDefault()?.Id ?? ""
        });
        Bind();
        Ui.Toast("Follow-effect gemaakt");
    }

    private void Test_Click(object sender, RoutedEventArgs e) => EventManagerPage.Test(Primary);

    private void Simulate_Click(object sender, RoutedEventArgs e)
    {
        var entry = App.Controller.Simulate(StreamEvent.Test(EventTypes.Follow, user: "testvolger" + Random.Shared.Next(100, 999)));
        if (entry != null) Ui.Toast($"Follow gesimuleerd: {entry.StatusLabel}" + (string.IsNullOrEmpty(entry.ShowName) ? "" : $" → {entry.ShowName}"), ToastKind.Info);
        RefreshFollowers();
    }

    private void EditShow_Click(object sender, RoutedEventArgs e) => Ui.Navigate("shows");

    private void ResetSession_Click(object sender, RoutedEventArgs e)
    {
        App.Controller.Rules.ResetSession();
        Ui.Toast("Follower-lijst gewist – iedereen kan weer een effect triggeren");
    }
}
