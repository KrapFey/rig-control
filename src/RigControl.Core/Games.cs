using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RigControl.Core;

/// <summary>
/// Knows a game when one runs, with no list to keep by hand: a program is a game when Windows has it on its own list
/// of games (the one Game Bar and Game Mode use), or when it runs from a game library folder (Steam, Epic, GOG,
/// Ubisoft, Xbox). Crash reporters, launchers and the like inside those folders don't count.
/// </summary>
public sealed class GameDetector
{
    static readonly TimeSpan LookAgainEvery = TimeSpan.FromMinutes(5); // a game installed meanwhile
    HashSet<string> _exes = new(StringComparer.OrdinalIgnoreCase);
    List<string> _folders = [];
    long _lookedAt = -1;
    readonly Dictionary<int, (string Name, bool Game)> _seen = []; // per process id: asked once, not every pass

    /// <summary>The name of a game among <paramref name="processes"/>, or null. Call from one thread at a time.</summary>
    public string? Running(IEnumerable<Process> processes)
    {
        if (_lookedAt < 0 || Stopwatch.GetElapsedTime(_lookedAt) > LookAgainEvery)
        {
            (_exes, _folders) = Find();
            _lookedAt = Stopwatch.GetTimestamp();
            _seen.Clear();
        }
        string? found = null;
        var alive = new HashSet<int>();
        foreach (var p in processes)
        {
            string name;
            try { name = p.ProcessName; } catch { continue; } // gone meanwhile
            alive.Add(p.Id);
            if (!_seen.TryGetValue(p.Id, out var seen) || seen.Name != name) // a new process (ids are reused)
                _seen[p.Id] = seen = (name, ImagePath(p.Id) is { } path && IsGame(path, _exes, _folders));
            if (seen.Game) found ??= name;
        }
        foreach (var id in _seen.Keys.Where(id => !alive.Contains(id)).ToList()) _seen.Remove(id);
        return found;
    }

    // Parts of file names that mark what runs beside a game, not the game.
    static readonly string[] NotGames = ["crash", "launcher", "unins", "redist", "setup", "anticheat", "helper", "overlay", "updater", "bootstrap", "report", "install"];
    // Steam keeps a few programs that aren't games in its library.
    static readonly string[] NotGameFolders = [@"\wallpaper_engine\", @"\SteamVR\", @"\Steamworks Shared\"];

    /// <summary>Whether the program at <paramref name="path"/> is a game, given the known game programs and library folders.</summary>
    public static bool IsGame(string path, IReadOnlySet<string> exes, IEnumerable<string> folders)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        if (NotGames.Any(n => file.Contains(n, StringComparison.OrdinalIgnoreCase))) return false;
        if (NotGameFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))) return false;
        return exes.Contains(path) || folders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The game programs Windows knows and the game library folders on this PC. A source that isn't there adds nothing.</summary>
    public static (HashSet<string> Exes, List<string> Folders) Find()
    {
        var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        void Folder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar); } catch { }
        }
        static void Try(Action a) { try { a(); } catch { } }

        // Windows' own list: every game it has recognised when it ran.
        Try(() =>
        {
            using var store = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var child in store?.GetSubKeyNames() ?? [])
            {
                using var key = store!.OpenSubKey(child);
                if (key?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0) exes.Add(exe);
            }
        });
        // Steam: every library's steamapps\common.
        Try(() =>
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is not string root) return;
            foreach (var library in SteamLibraries(File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"))))
                Folder(Path.Combine(library, "steamapps", "common"));
        });
        // Epic: a manifest per installed game.
        Try(() =>
        {
            var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
                Try(() =>
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(item));
                    if (doc.RootElement.TryGetProperty("InstallLocation", out var where)) Folder(where.GetString());
                });
        });
        // GOG and Ubisoft: a registry key per installed game.
        foreach (var (key, value) in new[] { (@"SOFTWARE\WOW6432Node\GOG.com\Games", "path"), (@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", "InstallDir") })
            Try(() =>
            {
                using var games = Registry.LocalMachine.OpenSubKey(key);
                foreach (var id in games?.GetSubKeyNames() ?? [])
                {
                    using var game = games!.OpenSubKey(id);
                    Folder(game?.GetValue(value) as string);
                }
            });
        // Xbox app / Game Pass: an XboxGames folder on each drive it installs to.
        Try(() =>
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
                if (Directory.Exists(Path.Combine(drive.Name, "XboxGames"))) Folder(Path.Combine(drive.Name, "XboxGames"));
        });
        return (exes, folders);
    }

    /// <summary>The library folders in Steam's libraryfolders.vdf (its "path" lines; backslashes are doubled there).</summary>
    public static List<string> SteamLibraries(string vdf) =>
        [.. Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))];

    /// <summary>
    /// Where a process's program is. Asked with the least access there is, which also works for games an anti-cheat
    /// guards (reading their modules, as Process.MainModule does, is refused).
    /// </summary>
    static string? ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var text = new StringBuilder(1024);
            int size = text.Capacity;
            return QueryFullProcessImageName(handle, 0, text, ref size) ? text.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
