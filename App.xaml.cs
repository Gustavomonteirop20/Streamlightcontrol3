using System.Windows;
using System.Windows.Threading;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2;

public partial class App : Application
{
    private Mutex? _singleInstance;
    public static AutomationController Controller { get; private set; } = null!;
    public static UiState State { get; private set; } = null!;
    public static Updater Updater { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Twee keer de app = twee keer poort 4002 en dubbele lichtshows. Dat voorkomen we.
        _singleInstance = new Mutex(true, "StreamLightControl.SingleInstance", out var isFirst);
        if (!isFirst && e.Args.Contains("--restart"))
        {
            // Herstart na terugzetten van een back-up: wacht tot de vorige instantie gesloten is.
            try { isFirst = _singleInstance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { isFirst = true; }
        }
        if (!isFirst)
        {
            MessageBox.Show("Stream Light Control draait al. Kijk in je taakbalk.", "Stream Light Control", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUiException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Controller?.Errors.Report("app.crash", Controller.Errors.Describe(args.Exception));
        };

        Controller = new AutomationController();
        Controller.Dispatch = action => Dispatcher.BeginInvoke(action);
        Controller.Initialize();
        // Thema (kerst, herfst, zomer…) vóór het eerste venster, zodat alle stijlen de juiste kleuren pakken
        try { ThemeApplier.Apply(this, AppThemes.Resolve(Controller.Settings.General.Theme, DateTime.Today)); }
        catch (Exception ex) { Controller.Errors.Report("app.crash", "Thema: " + ex.Message); }
        State = new UiState(Controller);

        Updater = new Updater(Controller.Store.DataDirectory);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (e.Args.Contains("--updated"))
            Ui.Toast($"Bijgewerkt naar versie {Updater.CurrentVersion}", ToastKind.Success);
        // Automatisch bijwerken bij het opstarten (vóór er iets live gaat)
        if (await Updater.RunStartupAsync()) return;

        await Controller.StartLastModeAsync();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Controller?.Errors.Report("app.crash", Controller.Errors.Describe(e.Exception));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Controller?.Dispose(); } catch { }
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
