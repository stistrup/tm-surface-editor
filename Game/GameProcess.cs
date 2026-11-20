using System.Diagnostics;

namespace TmSurfaceEditor;

// the running game: it loads items only at start-up
internal static class GameProcess
{
    // UTC start of the earliest running game, null if none (Windows: "Trackmania", Proton: "Trackmania.exe")
    public static DateTime? StartedAt()
    {
        DateTime? first = null;
        foreach (var process in Process.GetProcesses())
            using (process)
                try
                {
                    if (process.ProcessName is not ("Trackmania" or "Trackmania.exe")) continue;
                    var start = process.StartTime.ToUniversalTime();
                    if (first is null || start < first) first = start;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return first;
    }

    // our item files written since `since` that the running game started before: it holds the old ones
    public static List<string> ItemsNeedingRestart(DateTime since)
    {
        if (StartedAt() is not { } game || !Directory.Exists(Paths.Items)) return [];
        var after = game > since ? game : since;
        return [.. Directory.EnumerateFiles(Paths.Items, "*.Item.gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
            .Where(file => File.GetLastWriteTimeUtc(file) >= after)];
    }

    // the plugin rewrites it every second while loaded
    public static bool PluginAlive()
        => new FileInfo(Path.Combine(Paths.ExtractPlugin, "alive.txt")) is { Exists: true } alive
            && DateTime.UtcNow - alive.LastWriteTimeUtc < TimeSpan.FromSeconds(10);

    // the plugin reloads them in the running game; returns the ones it didn't (all of them without the plugin)
    public static List<string> RefreshInGame(List<string> items, TimeSpan wait)
    {
        if (items.Count == 0 || !PluginAlive()) return items;
        var request = Path.Combine(Paths.ExtractPlugin, "refresh-request.txt");
        var result = Path.Combine(Paths.ExtractPlugin, "refresh-result.txt");
        File.Delete(result);
        File.WriteAllLines(request, items.Select(file => Path.GetRelativePath(Paths.TrackmaniaUser, file).Replace('/', '\\')));
        var until = DateTime.UtcNow + wait;
        while (!File.Exists(result) && DateTime.UtcNow < until) Thread.Sleep(250);
        // a request still lying there would run whenever the plugin loads next: pointless then
        if (!File.Exists(result)) { File.Delete(request); return items; }
        Thread.Sleep(100);
        var done = File.ReadAllLines(result).Where(line => line.StartsWith("ok ")).Select(line => line[3..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(result).Where(line => !line.StartsWith("ok "))) Console.Error.WriteLine($"game refresh: {line}");
        return [.. items.Where(file => !done.Contains(Path.GetRelativePath(Paths.TrackmaniaUser, file).Replace('/', '\\')))];
    }
}
