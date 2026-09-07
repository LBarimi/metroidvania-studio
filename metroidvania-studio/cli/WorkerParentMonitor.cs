using System.Diagnostics;
using System.Globalization;

namespace MetroidvaniaStudio.Cli;

internal static class WorkerParentMonitor
{
    public static void Start()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("METROIDVANIA_STUDIO_WORKER_PARENT_PID"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || !long.TryParse(Environment.GetEnvironmentVariable("METROIDVANIA_STUDIO_WORKER_PARENT_TICKS"), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    long actual = parent.StartTime.ToUniversalTime().Ticks;
                    long tolerance = OperatingSystem.IsWindows() ? 0 : TimeSpan.FromSeconds(3).Ticks;
                    // Unix process timestamps derive from boot wall time and can differ slightly between callers.
                    if (parent.HasExited || Math.Abs(actual - ticks) > tolerance) Environment.Exit(130);
                }
                catch (ArgumentException) { Environment.Exit(130); }
                catch (InvalidOperationException) { Environment.Exit(130); }
                catch (System.ComponentModel.Win32Exception) { Environment.Exit(130); }
                await Task.Delay(100);
            }
        });
    }
}
