using System.Windows;
using System.Windows.Controls;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Views;

public partial class QueuePage : UserControl, IPageActivated
{
    private UiState S => App.State;

    public QueuePage()
    {
        InitializeComponent();
        DataContext = S;
        S.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(UiState.QueuePaused)) UpdatePaused(); };
        UpdatePaused();
    }

    public void OnActivated() => UpdatePaused();

    private void UpdatePaused()
    {
        var paused = S.QueuePaused;
        PausedBanner.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => App.Controller.Queue.SkipCurrent();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        App.Controller.Queue.StopCurrent();
        Ui.Toast("Effect gestopt en wachtrij gepauzeerd", ToastKind.Info);
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        App.Controller.Queue.Resume();
        Ui.Toast("Wachtrij hervat");
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (S.QueueCount == 0) { Ui.Toast("De wachtrij is al leeg.", ToastKind.Info); return; }
        if (!await Ui.Confirm("Wachtrij leegmaken?", $"{S.QueueCount} effect(en) worden overgeslagen. Het lopende effect speelt nog af.", "Leegmaken", danger: true)) return;
        App.Controller.Queue.Clear();
        Ui.Toast("Wachtrij leeggemaakt");
    }

    private void Up_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.CommandParameter is QueueItem q) App.Controller.Queue.Move(q.Id, -1); }
    private void Down_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.CommandParameter is QueueItem q) App.Controller.Queue.Move(q.Id, 1); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.CommandParameter is QueueItem q) App.Controller.Queue.Remove(q.Id); }

    private void Priority_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Bij het opbouwen van de lijst is de waarde gelijk aan de huidige prioriteit: dan niets doen.
        if (sender is ComboBox { Tag: QueueItem item, SelectedValue: int p } && p != item.Priority)
            App.Controller.Queue.SetPriority(item.Id, p);
    }
}
