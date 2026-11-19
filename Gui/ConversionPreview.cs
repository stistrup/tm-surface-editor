using System.Diagnostics;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmSurfaceEditor.PiecePlanner;

namespace TmSurfaceEditor;

// the page's preview: the planner's decisions plus what the view needs, cached per map and settings;
// pieces nobody built yet are built in the background and drawn once they're there
internal sealed class ConversionPreview(string dataDir)
{
    public sealed record Result(List<Option> Options, Dictionary<int, Decision> Blocks, Dictionary<int, Decision> Items, Dictionary<int, Decision> Cameras, int AddedItems, long Milliseconds,
        Dictionary<string, string> Swaps, Dictionary<string, Look> Looks, string AddedScene,
        // originals drawn for real in the added scene (or gone): hidden in the view
        List<int> HiddenBlocks, List<int> HiddenItems,
        // builds finished before this result; a later one means the missing pieces are there now
        int Builds,
        // settings fields that didn't read, and picks for pieces this map doesn't have
        List<string> Problems, List<string> StalePicks)
    {
        // per kind what a piece can be picked to become (data/picks.jsonc)
        public Dictionary<string, List<PickCatalog.Choice>> Choices { get; init; } = [];
        public Dictionary<string, string> KindLabels { get; init; } = [];
        public PickCatalog.Words? Words { get; init; }
        // waiting for their items to be built, by index in the map's own order
        public List<int> PendingBlocks { get; init; } = [];
        public List<int> PendingItems { get; init; } = [];
        public MapSize? Size { get; init; }
    }

    // the pieces previews needed and nobody had built, built in the background
    public sealed record BuildStatus(bool Running, int Done, int Total, int Builds);

    // a material the swaps lead to, as the page's scene materials list it (images relative to GameData)
    public sealed record Look(string Name, string? Shader, List<LookTexture> Textures, List<string>? Tints);

    public sealed record LookTexture(string Role, string Name, string? Image);

    // the theme's materials.jsonc with its own sides, and every material it names
    private (Dictionary<string, string> Swaps, Dictionary<string, Look> Looks) ThemeLooks(BlockCatalog shapes)
    {
        var swaps = Theme.Load(dataDir).Map(null);
        var view = new MapView(shapes);
        var gameData = Path.GetDirectoryName(Paths.ExtractedStadium)!;
        var looks = new Dictionary<string, Look>();
        foreach (var to in swaps.Values.Where(v => v is not ("" or "native")).Distinct())
        {
            var m = view.OwnMaterial(to);
            looks[to] = new Look(m.Name, m.Shader, m.Textures.Select(t => new LookTexture(t.Role, t.Name, t.Image is null ? null : Path.GetRelativePath(gameData, t.Image))).ToList(), m.Tints);
        }
        return (swaps, looks);
    }

    private readonly PiecePlanner planner = new(dataDir);
    private (Dictionary<string, string> Swaps, Dictionary<string, Look> Looks) themeLooks = ([], []);

    private readonly SemaphoreSlim one = new(1);
    private readonly Dictionary<string, Result> recent = [];
    // the last runs' scene drawing, to redraw once their pieces are built; few, they hold the converted map
    private readonly List<(string Key, Func<(Result, (byte[] Manifest, byte[] Geometry))> Draw)> drawings = [];
    // the items a run adds, as a small scene of their own; by Result.AddedScene
    private readonly Dictionary<string, (byte[] Manifest, byte[] Geometry)> addedScenes = [];
    private Task building = Task.CompletedTask;
    private int buildDone, buildTotal, builds;
    // the map the page asked for last: runs for others are dropped, their builds stop
    private volatile string? wanted;
    private string? buildsFor;
    private CancellationTokenSource buildCancel = new();
    private readonly Lock buildLock = new();

    public sealed class Superseded() : Exception("another map was asked for");

    private void Want(string mapPath)
    {
        wanted = mapPath;
        lock (buildLock) if (buildsFor is not null && buildsFor != mapPath) buildCancel.Cancel();
    }

    public BuildStatus Building() => new(!building.IsCompleted, buildDone, buildTotal, builds);

    // under `one`: whatever the runs so far planned and found unbuilt
    private void StartBuilds(string mapPath)
    {
        if (!building.IsCompleted || planner.Builder is not { PlannedCount: > 0 } builder) return;
        var jobs = builder.TakePlanned();
        (buildDone, buildTotal) = (0, jobs.Count);
        CancellationToken cancel;
        lock (buildLock)
        {
            (buildsFor, buildCancel) = (mapPath, new());
            cancel = buildCancel.Token;
        }
        building = Task.Run(() =>
        {
            // the skipped ones are planned again when their map is previewed
            try
            {
                // runs that planned while this built: theirs follow at once, or they'd wait for the next run
                for (var done = 0; jobs.Count > 0; jobs = builder.TakePlanned())
                {
                    buildTotal = done + jobs.Count;
                    var before = done;
                    ItemBuilder.RunBuilds(jobs, Console.Out, n => buildDone = before + n, cancel);
                    done += jobs.Count;
                }
            }
            catch (OperationCanceledException) { Console.WriteLine($"preview builds: stopped after {buildDone} of {buildTotal}, another map opened"); }
            catch (Exception e) { Console.Error.WriteLine($"preview builds: {e.Message}"); }
            // results from before point at pieces that exist now
            Interlocked.Increment(ref builds);
        });
    }

    public (byte[] Manifest, byte[] Geometry)? AddedScene(string id)
    {
        lock (addedScenes) return addedScenes.TryGetValue(id, out var scene) ? scene : null;
    }

    // Settings: the working copy, what a convert uses now; Seed: no settings of its own
    public sealed record MapState(System.Text.Json.Nodes.JsonObject Settings, string From, System.Text.Json.Nodes.JsonObject Seed, List<Option> Defaults, List<string> Unreadable,
        bool Dirty, bool CanUndo, bool CanRedo);

    // nothing worked out; the map's run with these settings starts in the background, so Preview is quick
    public MapState StateOf(string mapPath)
    {
        Want(mapPath);
        var header = Gbx.ParseHeaderNode<CGameCtnChallenge>(mapPath);
        var log = SettingsStore.Default;
        var (settings, from) = log.Current(header.MapUid);
        var document = log.Document(header.MapUid);
        var seed = MapSettings.Empty;
        _ = Task.Run(async () =>
        {
            try { await For(mapPath, settings); }
            catch (Superseded) { }
            catch (Exception e) { Console.Error.WriteLine($"preview warm-up {mapPath}: {e.Message}"); }
        });
        return new MapState(settings.Json, from, seed.Json, planner.DefaultOptions(), log.Problems, document.Dirty, document.CanUndo, document.CanRedo);
    }

    // as For, once the pieces it needs are built
    public async Task<Result> Settled(string mapPath, MapSettings settings)
    {
        for (;;)
        {
            var result = await For(mapPath, settings);
            while (Building().Running) await Task.Delay(500);
            if (Building().Builds == result.Builds) return result;
        }
    }

    public async Task<Result> For(string mapPath, MapSettings settings)
    {
        Want(mapPath);
        await one.WaitAsync();
        try
        {
            if (wanted != mapPath) throw new Superseded();
            var clock = Stopwatch.StartNew();
            if (planner.SetUpIfDataChanged())
            {
                recent.Clear();
                drawings.Clear();
                themeLooks = ThemeLooks(planner.Catalog);
            }
            var key = $"{mapPath}|{File.GetLastWriteTimeUtc(mapPath).Ticks}|{settings.Json.ToJsonString()}";
            if (recent.TryGetValue(key, out var known))
            {
                if (known.Builds == builds) return known with { Milliseconds = clock.ElapsedMilliseconds };
                // the plan stands (meshes are made while planning); only the built pieces are new
                if (drawings.FirstOrDefault(d => d.Key == key).Draw is { } draw)
                {
                    var (redrawn, again) = draw();
                    Remember(key, redrawn, again);
                    return redrawn with { Milliseconds = clock.ElapsedMilliseconds };
                }
            }
            var plan = planner.Run(mapPath, settings);
            var drawing = Drawing(mapPath, plan, key, clock);
            var (result, scene) = drawing();
            StartBuilds(mapPath);
            Remember(key, result, scene);
            drawings.RemoveAll(d => d.Key == key);
            drawings.Add((key, drawing));
            if (drawings.Count > 3) drawings.RemoveAt(0);
            return result with { Milliseconds = clock.ElapsedMilliseconds };
        }
        finally { one.Release(); }
    }

    private void Remember(string key, Result result, (byte[] Manifest, byte[] Geometry) scene)
    {
        if (recent.Count >= 16)
        {
            recent.Clear();
            lock (addedScenes) addedScenes.Clear();
        }
        lock (addedScenes) addedScenes[result.AddedScene] = scene;
        recent[key] = result;
    }

    // what the conversion changed, drawn from the converted map where a real mesh exists; the originals those replace get hidden
    private Func<(Result, (byte[] Manifest, byte[] Geometry))> Drawing(string mapPath, Plan plan, string key, Stopwatch clock)
    {
        var (map, run, before) = (plan.Map, plan.Run, plan.Before);
        var blockOrigin = before.Blocks.Index().ToDictionary(b => (object)b.Item, b => b.Index, ReferenceEqualityComparer.Instance);
        var itemOrigin = before.Items.Index().ToDictionary(o => (object)o.Item.Item, o => o.Index, ReferenceEqualityComparer.Instance);
        var converted = map.GetBlocks().ToList();
        var convertedItems = (map.AnchoredObjects ?? []).ToList();
        var now = new HashSet<object>(convertedItems, ReferenceEqualityComparer.Instance);
        var changedBlocks = ChangedBlocks(run, before, converted, blockOrigin);
        var changedItems = convertedItems.Where(o => !itemOrigin.ContainsKey(o) || plan.Items[itemOrigin[o]].Outcome != "unchanged").ToHashSet();
        var drawCatalog = planner.Catalog;
        var environment = drawCatalog.Environment;
        var picks = planner.Picks;
        return () =>
        {
            // shared with later runs of other maps: native materials resolve per environment
            drawCatalog.Environment = environment;
            var part = new MapView(drawCatalog).BuildPart(map, changedBlocks, changedItems);
            var stillThere = converted.ToHashSet(ReferenceEqualityComparer.Instance);
            var hiddenBlocks = before.Blocks.Index().Where(b => !stillThere.Contains(b.Item)).Select(b => b.Index).ToHashSet();
            var hiddenItems = before.Items.Index().Where(o => !now.Contains(o.Item.Item)).Select(o => o.Index).ToHashSet();
            // origin: the original it replaces, for the properties picture
            foreach (var info in part.Instances.Select(i => i.Info))
            {
                if (info.TryGetValue("block", out var b) && blockOrigin.TryGetValue(converted[(int)b], out var ob)) { hiddenBlocks.Add(ob); info["originBlock"] = ob; }
                // a gate's ground goes with the gate
                if (info.TryGetValue("block", out b) && run.GroundOf.TryGetValue(converted[(int)b], out var gate) && blockOrigin.TryGetValue(gate, out var og)) info["originBlock"] = og;
                if (info.TryGetValue("item", out var it) && itemOrigin.TryGetValue(convertedItems[(int)it], out var oi)) { hiddenItems.Add(oi); info["originItem"] = oi; }
            }
            var addedCount = convertedItems.Count(o => !itemOrigin.ContainsKey(o));
            var sceneId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}|{builds}")))[..16];
            var scene = SceneBundle.InMemory(part);
            var result = new Result(plan.Options, plan.Blocks, plan.Items, plan.Cameras, addedCount, clock.ElapsedMilliseconds, themeLooks.Swaps, themeLooks.Looks,
                sceneId, hiddenBlocks.Order().ToList(), hiddenItems.Order().ToList(), builds, plan.Problems, plan.StalePicks)
                {
                    Choices = picks.Choices,
                    KindLabels = picks.KindLabels,
                    Words = picks.Labels,
                    PendingBlocks = [.. before.Blocks.Index().Where(b => run.Waiting.TryGetValue(b.Item, out var file) && run.Items.Unbuilt(file)
                        || run.Customs.TryGetValue(b.Item, out var c) && run.Items.Unbuilt(c.ItemPath)).Select(b => b.Index)],
                    PendingItems = [.. before.Items.Index().Where(o => run.WaitingItems.TryGetValue(o.Item.Item, out var file) && run.Items.Unbuilt(file)).Select(o => o.Index)],
                    Size = MapSize.Of(mapPath, run, blockOrigin, itemOrigin),
                };
            return (result, scene);
        };
    }

    // flags too: a pillar set from ground to air draws other clips
    private static HashSet<CGameCtnBlock> ChangedBlocks(ConversionRun run, Before before, List<CGameCtnBlock> converted, Dictionary<object, int> blockOrigin)
    {
        bool Changed(CGameCtnBlock b) => !blockOrigin.TryGetValue(b, out var o) || run.Customs.ContainsKey(b) || run.Renamed.Contains(b) || b.Flags != before.Flags[o]
            || (b.Direction, b.AbsolutePositionInMap, b.YawPitchRoll) != before.Placements[o];
        var changed = converted.Where(Changed).ToHashSet();
        // the terrain a native ground block hid shows again under its custom block
        var underCustoms = run.Customs.Keys.Where(b => b.IsGround && run.Cells.ContainsKey(b) && !(blockOrigin.TryGetValue(b, out var o) && BlockNames.OnTerrainShape(before.Names[o])))
            .SelectMany(b => run.Cells[b]).ToHashSet();
        changed.UnionWith(converted.Where(b => run.Shapes.IsTerrain(b.Name) && run.Cells.TryGetValue(b, out var cells) && cells.Any(underCustoms.Contains)));
        // a pillar's walls follow the block it holds up
        var parents = PillarParents.Find(run.Shapes, converted.Where(run.Cells.ContainsKey).Select(b => (b, (IReadOnlyList<Int3>)run.Cells[b])));
        changed.UnionWith(parents.Where(p => changed.Contains(p.Value)).Select(p => p.Key));
        return changed;
    }
}
