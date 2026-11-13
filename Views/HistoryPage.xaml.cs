using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Views;

public partial class HistoryPage : UserControl, IPageActivated
{
    private UiState S => App.State;
    private readonly ListCollectionView _view;
    private string _type = "";
    private string _query = "";
    private readonly DispatcherTimer _searchDelay;

    public HistoryPage()
    {
        InitializeComponent();
        _view = new ListCollectionView(S.History) { Filter = Matches };
        List.ItemsSource = _view;
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Refresh(); };
        S.History.CollectionChanged += (_, _) => UpdateCount();
        UpdateCount();
    }

    public void OnActivated() => Refresh();

    private bool Matches(object o)
    {
        if (o is not HistoryEntry h) return false;
        if (HideTests?.IsChecked == true && h.IsTest) return false;
        if (_type == "errors" && h.Status != ProcessingStatus.Failed) return false;
        if (_type is not ("" or "errors") && h.Type != _type) return false;
        if (_query.Length == 0) return true;
        return h.User.Contains(_query, StringComparison.OrdinalIgnoreCase)
               || h.GiftName.Contains(_query, StringComparison.OrdinalIgnoreCase)
               || h.ShowName.Contains(_query, StringComparison.OrdinalIgnoreCase)
               || h.Detail.Contains(_query, StringComparison.OrdinalIgnoreCase);
    }

    private void Refresh()
    {
        _view.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (CountText == null) return;
        var shown = _view.Count;
        CountText.Text = shown == S.History.Count ? $"{shown:N0} events" : $"{shown:N0} van {S.History.Count:N0} events";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (S.History.Count == 0) EmptyText.Text = "Nog geen events ontvangen. Alles wat via BetterTok binnenkomt, verschijnt hier.";
        else EmptyText.Text = "Geen events gevonden met deze filters.";
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text.Trim();
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) _type = tag;
        if (_view != null) Refresh();
    }

    private void HideTests_Changed(object sender, RoutedEventArgs e) { if (_view != null) Refresh(); }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!await Ui.Confirm("Geschiedenis wissen?", "Alle events in Event History worden verwijderd. Dit kan niet ongedaan worden gemaakt. (Het ruwe events.log blijft bestaan.)", "Wissen", danger: true)) return;
        App.Controller.History.Clear();
        Ui.Toast("Geschiedenis gewist");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV-bestand (*.csv)|*.csv", FileName = $"event-history-{DateTime.Now:yyyy-MM-dd}.csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder("Tijdstip;Gebruiker;Event;Gift;Aantal;Coins;Lichtshow;Status;Details;Test\r\n");
            static string Q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
            foreach (var h in _view.Cast<HistoryEntry>())
                sb.Append(string.Join(";", Q(h.Time.ToString("yyyy-MM-dd HH:mm:ss")), Q(h.User), Q(h.TypeLabel), Q(h.GiftName), h.GiftCount,
                    h.Coins?.ToString() ?? "", Q(h.ShowName), Q(h.StatusLabel), Q(h.Detail), h.IsTest ? "ja" : "nee")).Append("\r\n");
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            Ui.Toast("Geëxporteerd naar " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { Ui.Toast("Exporteren mislukt: " + ex.Message, ToastKind.Error); }
    }
}
