using System.Runtime.InteropServices;

namespace Mailcast.HeadEnd.Slot;

/// <summary>Whether the system clock is synchronised, and why the answer is what it is.</summary>
public sealed record ClockState(bool? Synchronised, string Detail);

/// <summary>Says whether the system clock can be trusted to put the slot at the right time.</summary>
public interface IClockSync
{
    ClockState Check();
}

/// <summary>
/// The kernel's own view, from <c>adjtimex(2)</c> with nothing changed: it returns TIME_ERROR (5)
/// while the clock is not synchronised, whichever of systemd-timesyncd, chrony or ntpd keeps it.
/// That is what timedatectl reports as NTPSynchronized, without needing the D-Bus service.
/// </summary>
public sealed partial class KernelClockSync : IClockSync
{
    private const int TimeError = 5;

    /// <inheritdoc />
    public ClockState Check()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new ClockState(null, "not Linux, so the kernel cannot be asked");
        }
        try
        {
            // struct timex is about 200 octets; zeroed, so modes is 0 and nothing is set.
            var timex = new byte[512];
            int state = Adjtimex(timex);
            return state switch
            {
                < 0 => new ClockState(null, $"adjtimex failed (errno {Marshal.GetLastPInvokeError()})"),
                TimeError => new ClockState(false, "the kernel says the clock is not synchronised (adjtimex TIME_ERROR)"),
                _ => new ClockState(true, $"synchronised (adjtimex state {state})"),
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return new ClockState(null, $"adjtimex is not available: {e.Message}");
        }
    }

    [LibraryImport("libc", EntryPoint = "adjtimex", SetLastError = true)]
    private static partial int Adjtimex([In, Out] byte[] timex);
}
