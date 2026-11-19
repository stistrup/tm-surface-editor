using GBX.NET;
using GBX.NET.Engines.Game;
using static TmSurfaceEditor.CliArgs;

namespace TmSurfaceEditor;

// convert, watch: the options the GUI's Convert popup has
internal sealed class ConvertCommand(string dataDir)
{
    public const string Usage = """
          convert <map.Gbx | folder>... [options]
              Convert maps to ice, each to "<map name> (ICE).Map.Gbx"; the report goes with the run (settings/runs).
              --folder <dir>        where they go: a folder under Maps, or an absolute path (default: ICE in progress)
              --name <map name>     the converted map's name (one map only); the file is named after it
              --suffix <text>       after each default name instead of "(ICE)"
              --fake-author-time    validated with a 1:00:00.000 author time, so it can go to a club without driving it
              --me-as-author        your account (testrunAuthor in local.jsonc) as the map's author
              --rebuild-blocks      write the custom blocks again even when their items didn't change
              --overwrite           replace a map of the same file name; it keeps that map's UID
              --settings <from>     current (the map's working copy from the GUI, else its saved settings), saved,
                                    a settings file, or a run id or file
              --preset <name>       a preset from data/presets.jsonc; alone it's all the map converts by, with --settings it goes on top
                                    One of the two is needed: nothing converts by default.
              --set <option>=true|false   override an option for this run (repeatable)
              A new file name gets a UID of its own (from the original map and the name). Every conversion is
              logged with its settings (see runs).
          watch [options]
              Keep converting maps from the drop folder as they change (same options, no --name; always overwrites).
        """;

    public int Run(string[] rest)
    {
        if (rest.Length == 0 || rest.Any(a => a is "-h" or "--help"))
        {
            Console.WriteLine("Usage:\n" + Usage);
            return rest.Length == 0 ? 1 : 0;
        }
        if (Parse(ref rest, allowName: true) is not { } choices) return 2;
        if (rest.FirstOrDefault(a => a.StartsWith('-')) is { } unknown) return Fail($"unknown option {unknown}; see convert --help");
        var maps = ExpandMaps(rest).ToList();
        if (maps.Count == 0) return Fail("no maps given");
        if (choices.Name is not null && maps.Count != 1) return Fail("--name takes one map");
        return Report(ConvertWithBar(maps, choices));
    }

    // polling: the game writes maps in steps
    public int Watch(string[] rest)
    {
        if (rest.Any(a => a is "-h" or "--help")) { Console.WriteLine("Usage:\n" + Usage); return 0; }
        if (Parse(ref rest, allowName: false) is not { } parsed) return 2;
        if (rest.Length > 0) return Fail($"watch takes options only, not {rest[0]}");
        var choices = parsed with { Overwrite = true };
        Console.WriteLine($"Watching {Paths.DropFolder} (Ctrl+C to stop)");
        var sizes = new Dictionary<string, long>();
        while (true)
        {
            Directory.CreateDirectory(Paths.DropFolder);
            var ready = ExpandMaps([Paths.DropFolder]).Where(p =>
            {
                var size = new FileInfo(p).Length;
                var stable = sizes.TryGetValue(p, out var last) && last == size;
                sizes[p] = size;
                var output = DefaultTarget(p, choices.Folder, choices.Suffix);
                return stable && (output is null || !File.Exists(output) || File.GetLastWriteTimeUtc(output) < File.GetLastWriteTimeUtc(p));
            }).ToList();
            if (ready.Count > 0) Report(ConvertWithBar(ready, choices));
            Thread.Sleep(3000);
        }
    }

    private static ConvertChoices? Parse(ref string[] rest, bool allowName)
    {
        bool Flag(ref string[] args, string name)
        {
            var on = args.Contains(name);
            args = args.Where(a => a != name).ToArray();
            return on;
        }
        var fake = Flag(ref rest, "--fake-author-time");
        var me = Flag(ref rest, "--me-as-author");
        var rebuild = Flag(ref rest, "--rebuild-blocks");
        var overwrite = Flag(ref rest, "--overwrite");
        var folder = Option(ref rest, "--folder");
        var name = allowName ? Option(ref rest, "--name") : null;
        var suffix = Option(ref rest, "--suffix")?.Trim();
        var from = Option(ref rest, "--settings");
        var preset = Option(ref rest, "--preset");
        if (from is null && preset is null)
        {
            Fail("which settings? --settings current (the GUI's working copy, else saved), --settings saved, a settings file or run id; or --preset <name>");
            return null;
        }
        var set = new List<(string, bool)>();
        while (Option(ref rest, "--set") is { } pair)
        {
            if (pair.Split('=') is not [var option, var value] || !bool.TryParse(value, out var on)) { Fail($"--set takes name=true|false, not '{pair}'"); return null; }
            try { new ConvertOptions().With(option, on); } catch (FormatException e) { Fail(e.Message); return null; }
            set.Add((option, on));
        }
        if (me && Paths.TestrunAuthorIfSet is null) { Fail($"--me-as-author needs \"testrunAuthor\" in {Paths.LocalSettings} (or set it in the GUI's Convert popup)"); return null; }
        if (name is not null && MapNames.FileName(name) is null) { Fail("--name can't hold < > : \" / \\ | ? * or end in a dot"); return null; }
        var dir = folder is null ? Paths.OutFolder : Path.IsPathRooted(folder) ? folder : Path.GetFullPath(Path.Combine(Paths.Maps, folder));
        return new ConvertChoices(dir, name, fake, me, rebuild, overwrite) { From = from ?? ConvertChoices.None, Set = set, Preset = preset, Suffix = suffix ?? MapNames.DefaultSuffix };
    }

    private List<MapResult> ConvertWithBar(IReadOnlyList<string> maps, ConvertChoices choices)
    {
        var progress = new ConvertProgress();
        using var bar = new ConsoleProgress(progress);
        return new ConvertBatch(dataDir, choices, progress, default).Run(maps);
    }

    private static int Report(List<MapResult> results)
    {
        foreach (var (map, done, error) in results)
        {
            Console.WriteLine($"{Path.GetFileName(map)}:");
            if (error is not null) { Console.Error.WriteLine($"  FAILED: {error}"); continue; }
            Console.WriteLine($"  settings: {done!.From}");
            foreach (var problem in done.Problems) Console.WriteLine($"  left out: {problem}");
            Console.WriteLine($"  {done.Summary} -> {done.Output}");
            Console.WriteLine($"  run {done.RunId}, report {done.Report}");
        }
        if (results.Select(r => r.Done?.MissingFiles ?? 0).DefaultIfEmpty().Max() is > 0 and var missing)
        {
            Console.WriteLine($"{missing} files the conversion needs aren't extracted; listed in the extract plugin's view-refs.txt.");
            Console.WriteLine("In game: Openplanet > Extract game files, then convert again (those items rebuild).");
        }
        return results.All(r => r.Error is null) ? 0 : 2;
    }

    private static string? DefaultTarget(string map, string folder, string suffix)
    {
        try
        {
            var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(map);
            return MapNames.FileName(MapNames.DefaultOutput(header.MapName, SettingsStore.Default.NameOf(header.MapUid), suffix)) is { } file ? MapOutput.Existing(folder, file) : null;
        }
        catch (Exception) { return null; }
    }

    // decisions only, nothing built or saved: times one map's conversion in planning mode
    public int Plan(string[] rest)
    {
        if (rest is not [var path]) return Fail("plan takes one map");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var shapes = new BlockCatalog(Paths.ExtractedStadium);
        var items = new ItemBuilder(dataDir) { Planning = true };
        var dataRules = ConversionRules.Load(dataDir);
        var converter = new MapConverter(BlockNames.Known(dataDir), dataRules, shapes, items);
        Console.WriteLine($"setup {clock.ElapsedMilliseconds} ms");
        for (var i = 1; i <= 3; i++)
        {
            clock.Restart();
            var map = Gbx.ParseNode<CGameCtnChallenge>(path);
            var parse = clock.ElapsedMilliseconds;
            var report = converter.Convert(map, new ConversionRules { RulesOn = true });
            Console.WriteLine($"run {i}: parse {parse} ms, decide {clock.ElapsedMilliseconds - parse} ms, custom blocks {report.UsedBlocks.Count}");
        }
        return 0;
    }
}
