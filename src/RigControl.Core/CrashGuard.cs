namespace RigControl.Core;

/// <summary>
/// Notices when RigControl ended without its own exit: a crash its crash handler never saw (WinUI's fail-fast kills
/// the process outright), or being killed. Fans would stay at their last speed then, so a watchdog brings
/// RigControl back, or hands the fans to the hardware if it keeps crashing; the next start says what happened.
/// </summary>
/// <remarks>
/// A run marks itself in a "session" file (its process id) when it starts and removes the mark when it exits
/// cleanly, including when Windows ends the session. A mark left by the process the watchdog watched means it
/// didn't get to exit.
/// </remarks>
public static class CrashGuard
{
    public const int MaxRestarts = 3;
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

    static string SessionFile => Path.Combine(AppConfig.Folder, "session");
    static string RestartsFile => Path.Combine(AppConfig.Folder, "restarts");

    /// <summary>
    /// Marks this run as started. Returns when the previous run started, if it ended without exiting cleanly since
    /// Windows last started (a mark from before then is a power cut or a forced shutdown, not something to report).
    /// </summary>
    public static DateTime? Begin(int pid, DateTime bootTime)
    {
        DateTime? unclean = null;
        try
        {
            if (File.Exists(SessionFile) && File.GetLastWriteTime(SessionFile) is var at && at > bootTime) unclean = at;
            Directory.CreateDirectory(AppConfig.Folder);
            File.WriteAllText(SessionFile, pid.ToString());
        }
        catch { /* no mark: no watchdog restart, which is the old behaviour */ }
        return unclean;
    }

    /// <summary>A clean exit (or Windows ending the session): nothing for the watchdog to do.</summary>
    public static void End(int pid)
    {
        try { if (MarkedPid() == pid) File.Delete(SessionFile); } catch { }
    }

    /// <summary>The process that marked a run as started and hasn't exited cleanly, if any.</summary>
    public static int? MarkedPid()
    {
        try { return int.TryParse(File.ReadAllText(SessionFile).Trim(), out var p) ? p : null; }
        catch { return null; }
    }

    public enum Action { None, Restart, ReleaseFans }

    /// <summary>What the watchdog does once the RigControl it watched has ended.</summary>
    /// <param name="recentRestarts">Restarts it already made within <see cref="RestartWindow"/>.</param>
    public static Action Decide(int watchedPid, int? markedPid, int recentRestarts, bool windowsEnding) =>
        windowsEnding || markedPid != watchedPid ? Action.None
        : recentRestarts < MaxRestarts ? Action.Restart
        : Action.ReleaseFans; // it keeps crashing: give the fans back to the hardware rather than loop

    /// <summary>Restarts made within <see cref="RestartWindow"/> before <paramref name="now"/>.</summary>
    public static int RecentRestarts(DateTime now) => Restarts().Count(t => now - t < RestartWindow);

    public static void NoteRestart(DateTime now)
    {
        try
        {
            var keep = Restarts().Where(t => now - t < RestartWindow).Append(now);
            File.WriteAllLines(RestartsFile, keep.Select(t => t.Ticks.ToString()));
        }
        catch { }
    }

    static List<DateTime> Restarts()
    {
        try { return [.. File.ReadAllLines(RestartsFile).Select(l => long.TryParse(l, out var t) ? new DateTime(t) : DateTime.MinValue)]; }
        catch { return []; }
    }
}
