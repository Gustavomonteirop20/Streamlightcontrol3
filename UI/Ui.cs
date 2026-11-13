using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StreamLightControl2;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>Hulpfuncties voor de interface: meldingen, bevestigingen, laadstatus en navigatie.</summary>
public static class Ui
{
    internal static MainWindow? Window;

    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static void Toast(string message, ToastKind kind = ToastKind.Success) => Window?.ShowToast(message, kind);

    /// <summary>Bevestiging voor belangrijke acties. Geeft true bij 'ja'.</summary>
    public static Task<bool> Confirm(string title, string message, string confirmText = "Doorgaan", bool danger = false) =>
        Window?.ShowConfirm(title, message, confirmText, danger) ?? Task.FromResult(false);

    /// <summary>Vraagt om een tekst (bijv. een nieuwe naam). Null = geannuleerd.</summary>
    public static Task<string?> Prompt(string title, string label, string initial) =>
        Window?.ShowPrompt(title, label, initial) ?? Task.FromResult<string?>(null);

    public static void Navigate(string page) => Window?.Navigate(page);

    /// <summary>
    /// Voert een actie uit met laadstatus op de knop (uitgeschakeld + tekst), en toont fouten netjes.
    /// </summary>
    public static async Task Busy(object? sender, string busyText, Func<Task> action, string? successToast = null)
    {
        var button = sender as Button;
        var oldContent = button?.Content;
        if (button != null) { button.IsEnabled = false; button.Content = busyText; }
        try
        {
            await action();
            if (successToast != null) Toast(successToast);
        }
        catch (Exception ex)
        {
            Toast(ex.Message, ToastKind.Error);
        }
        finally
        {
            if (button != null) { button.Content = oldContent; button.IsEnabled = true; }
        }
    }

    public static string Ago(DateTime? t)
    {
        if (t == null) return "—";
        var d = DateTime.Now - t.Value;
        return d.TotalSeconds < 5 ? "zojuist"
            : d.TotalSeconds < 60 ? $"{(int)d.TotalSeconds} s geleden"
            : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min geleden"
            : t.Value.ToString("HH:mm");
    }
}
