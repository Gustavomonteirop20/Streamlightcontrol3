using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StreamLightControl2.Controls;
using StreamLightControl2.Core.Models;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2.Views;

public partial class SpotifyPage : UserControl, IPageActivated
{
    private UiState S => App.State;
    private AutomationController C => App.Controller;
    private readonly DispatcherTimer _live;
    private long _lastBeatCount;
    private string _shownColor = "";
    private string _shownPalette = "";

    public SpotifyPage()
    {
        InitializeComponent();
        DataContext = S;
        // Live meter: alleen bijwerken als de pagina zichtbaar is
        _live = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _live.Tick += (_, _) => Tick();
        Loaded += (_, _) => { _live.Start(); Refresh(); };
        Unloaded += (_, _) => _live.Stop();
        S.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UiState.IsSpotifyMode) or nameof(UiState.SpotifyPaletteHex) or nameof(UiState.ErrorCount)) Refresh();
        };
        S.Errors.CollectionChanged += (_, _) => UpdateProblem();
        UpdateStyleHelp();
    }

    public void OnActivated() => Refresh();

    private void Refresh()
    {
        OffCard.Visibility = S.IsSpotifyMode ? Visibility.Collapsed : Visibility.Visible;
        OnCard.Visibility = S.IsSpotifyMode ? Visibility.Visible : Visibility.Collapsed;
        var pal = C.Spotify.Palette;
        var key = string.Join(",", pal);
        if (key != _shownPalette)
        {
            _shownPalette = key;
            PaletteList.ItemsSource = pal.ToList();
        }
        NoPalette.Visibility = pal.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProblem();
    }

    private void Tick()
    {
        var sp = C.Spotify;
        var running = sp.Running;
        LevelBar.Width = running ? Math.Max(0, ((FrameworkElement)LevelBar.Parent).ActualWidth * sp.Level) : 0;
        var color = running ? S.FrameHex : "#000000";
        if (color != _shownColor)
        {
            _shownColor = color;
            ColorStage.Background = HexToBrushConverter.Get(color);
        }
        if (sp.BeatCount != _lastBeatCount)
        {
            _lastBeatCount = sp.BeatCount;
            BeatDot.Fill = Ui.Res("SuccessBrush");
            BeatFlash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.18, 0, TimeSpan.FromMilliseconds(220)));
        }
        else if (sp.LastBeatAt is { } t && (DateTime.Now - t).TotalMilliseconds > 120)
            BeatDot.Fill = Ui.Res("Text3Brush");
        StatsText.Text = !running ? "Spotify-modus staat uit."
            : !sp.AudioAvailable ? $"Geluid kan niet worden gemeten – kleuren wisselen op een vast tempo · {sp.ChangeCount} kleurwissels"
            : (sp.Bpm > 0 ? $"{sp.Bpm:0} BPM (licht op de voorspelde beat) · " : "Tempo zoeken… · ")
              + $"{sp.ChangeCount} kleurwissels · {sp.FlashCount} flashbangs · {sp.BlackCount} zwarte stukjes";
        RaveBadge.Visibility = running && sp.RaveActive ? Visibility.Visible : Visibility.Collapsed;
        RaveText.Text = sp.RaveInDrop ? $"RAVE / ZAAG HERKEND · DROP · {sp.Bpm:0} BPM" : $"RAVE / ZAAG HERKEND · break · {sp.Bpm:0} BPM";
        if (string.Join(",", sp.Palette) != _shownPalette) Refresh();
    }

    private void UpdateProblem()
    {
        var problem = S.Errors.FirstOrDefault(x => x.Category == ErrorCategory.Spotify);
        ProblemBox.Visibility = problem == null || !S.IsSpotifyMode ? Visibility.Collapsed : Visibility.Visible;
        if (problem == null) return;
        ProblemTitle.Text = "Waarom lukt het niet? " + problem.Title;
        ProblemText.Text = problem.Explanation;
        ProblemSolution.Text = problem.Solution;
    }

    private void Style_Checked(object sender, RoutedEventArgs e)
    {
        if (StyleHelp == null) return;
        Dispatcher.BeginInvoke(UpdateStyleHelp);
    }

    private void UpdateStyleHelp()
    {
        var style = S.Settings.Spotify.Style;
        StyleHelp.Text = style switch
        {
            SpotifyStyle.Beat => "Festivalstijl: de kleur wisselt op de klap, alle 8 kleuren komen langs (steeds een paar, dan het volgende). In harde stukken zwarte stukjes en flashbangs, op de drop wit-zwart-wit.",
            SpotifyStyle.Calm => "Rustig: de 4 kleuren van het nummer lopen zacht in elkaar over. Fijn als achtergrondlicht.",
            _ => "Eén kleur per nummer. Op de beat wordt het licht even feller en zachter."
        };
        SensitivityPanel.Visibility = style == SpotifyStyle.Calm ? Visibility.Collapsed : Visibility.Visible;
        EffectsPanel.Visibility = style == SpotifyStyle.Calm ? Visibility.Collapsed : Visibility.Visible;
        BlackoutSwitch.Visibility = style == SpotifyStyle.Beat ? Visibility.Visible : Visibility.Collapsed;
        CalmPanel.Visibility = style == SpotifyStyle.Calm ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void EnableSpotify_Click(object sender, RoutedEventArgs e) => await Ui.Window!.SwitchModeAsync(AppMode.Spotify);
    private async void EnableStream_Click(object sender, RoutedEventArgs e) => await Ui.Window!.SwitchModeAsync(AppMode.Stream);
}
