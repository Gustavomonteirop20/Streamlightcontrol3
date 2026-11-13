using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace StreamLightControl2.Views;

/// <summary>Compacte weergave met alleen de belangrijkste bediening, voor tijdens het streamen.</summary>
public partial class StreamModeView : UserControl
{
    private UiState S => App.State;

    public StreamModeView()
    {
        InitializeComponent();
        DataContext = S;
        S.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UiState.IsRunning) or nameof(UiState.SafeMode) or nameof(UiState.IsLive)) Update();
        };
        Update();
    }

    private void Update()
    {
        StartStopButton.Content = S.IsRunning ? "Stop automatisering" : "Start automatisering";
        StartStopButton.Tag = S.IsRunning ? "" : "";
        StartStopButton.Style = (Style)FindResource(S.IsRunning ? "SecondaryButton" : "PrimaryButton");
        SafeButton.Content = S.SafeMode ? "Safe Mode staat AAN – klik om uit te zetten" : "Safe Mode (alles direct uit)";
        SafeButton.Tag = "";
        SafeButton.Style = (Style)FindResource(S.SafeMode ? "DangerButton" : "SecondaryButton");
        if (S.IsLive)
            LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        else LiveDot.BeginAnimation(OpacityProperty, null);
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (S.IsRunning) { App.Controller.StopAutomation(); Ui.Toast("Automatisering gestopt", ToastKind.Info); }
        else await Ui.Busy(sender, "Starten…", App.Controller.StartAutomationAsync, "Automatisering gestart");
        Update();
    }

    private async void Safe_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.Window != null) await Ui.Window.ToggleSafeModeAsync();
        Update();
    }

    private void Quick_Click(object sender, RoutedEventArgs e) => QuickActions.Run(sender, (string)((Button)sender).CommandParameter);
    private void Skip_Click(object sender, RoutedEventArgs e) => App.Controller.Queue.SkipCurrent();
    private void Exit_Click(object sender, RoutedEventArgs e) => Ui.Window?.SetStreamMode(false);
}
