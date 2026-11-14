using System.Runtime.InteropServices;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// Zet de Windows-timer tijdens een show op 1 ms (standaard ±15,6 ms), zodat wachten op de beat nauwkeurig is.
/// Op andere systemen doet dit niets.
/// </summary>
public static class TimerResolution
{
    public sealed class Handle : IDisposable
    {
        public bool Active { get; }
        internal Handle(bool active) => Active = active;
        public void Dispose() { if (Active) try { timeEndPeriod(1); } catch { } }
    }

    public static Handle High()
    {
        if (!OperatingSystem.IsWindows()) return new Handle(false);
        try { return new Handle(timeBeginPeriod(1) == 0); }
        catch { return new Handle(false); }
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
}
