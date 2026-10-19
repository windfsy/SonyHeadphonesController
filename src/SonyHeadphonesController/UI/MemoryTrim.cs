using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SonyCtrl.UI;

/// <summary>
/// Idle-time working-set trim: a compacting GC pass followed by
/// SetProcessWorkingSetSize(-1, -1) hands the pages the 15s refresh churn (icon pipeline,
/// backdrop blur buffers) ballooned the working set with back to the OS. Called only at
/// idle transitions (startup settle, panel hide) — never on a timer.
/// </summary>
internal static class MemoryTrim
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    private static bool _scheduled;

    /// <summary>
    /// Debounced one-shot: collapse repeats within the window (hide→show→hide) into a
    /// single pass, and push it past render teardown with a Background timer.
    /// </summary>
    public static void ScheduleTrim(TimeSpan delay)
    {
        if (_scheduled) return;
        _scheduled = true;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _scheduled = false;
            try
            {
                // Sweep-only (no compaction): a compacting pass copies live objects into
                // fresh segments and transiently RAISES committed memory by tens of MB,
                // which defeats the point.
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch
            {
                // Best-effort trim: a failure just means pages stay resident.
            }
        };
        timer.Start();
    }
}
