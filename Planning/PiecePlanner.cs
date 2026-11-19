using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// a dry run of the conversion: what each piece becomes, its group and what it could be picked to. Nothing drawn or built.
// One run at a time; an edited data file sets it up again
internal sealed class PiecePlanner(string dataDir)
{
    // Category: map, piece or look (ConvertOptions)
    public sealed record Option(string Name, bool Value, bool MapValue, string Category, string Description);

    // per block / item index in the map's own order
    public sealed record Decision(string Outcome, string? Rule, string? To)
    {
        // what the manual flow groups it under, and the key a pick is stored by
        public string Kind { get; init; } = "";
        public string Key { get; init; } = "";
        public string? Picked { get; init; }
        // its group for group conversions (data/groups.jsonc), e.g. roads/checkpoints; null: none
        public string? Group { get; init; }
        // its kind's picks that wouldn't change it
        public List<string>? Cannot { get; init; }
        // it has sides (penalty ground, deco, cliffs) a pick can choose for
        public bool Sides { get; init; }
    }

    // Map: converted in place, Before: as it was. Problems: settings fields that didn't read; StalePicks: picks for pieces this map doesn't have
    public sealed record Plan(List<Option> Options, Dictionary<int, Decision> Blocks, Dictionary<int, Decision> Items, Dictionary<int, Decision> Cameras,
        List<string> Problems, List<string> StalePicks, CGameCtnChallenge Map, ConversionRun Run, Before Before, List<ForcedCameras.Camera> ForcedCameras)
    {
        public IEnumerable<Decision> Pieces => Blocks.Values.Concat(Items.Values);
    }

    private MapConverter? converter;
    private ConversionRules? dataRules;
    // newest data file it was set up from
    private DateTime dataStamp;

    public BlockCatalog Catalog { get; private set; } = null!;
    public ItemBuilder Builder { get; private set; } = null!;
    public PickCatalog Picks { get; private set; } = null!;
    public GroupCatalog Groups { get; private set; } = null!;

    // true: set up anew, what was worked out before is stale
    public bool SetUpIfDataChanged()
    {
        var stamp = Directory.EnumerateFiles(dataDir, "*.jsonc", SearchOption.AllDirectories).Max(File.GetLastWriteTimeUtc);
        if (converter is not null && stamp == dataStamp) return false;
        dataRules = ConversionRules.Load(dataDir);
        Catalog = new BlockCatalog(Paths.ExtractedStadium);
        Builder = new ItemBuilder(dataDir) { Planning = true, MeshWhilePlanning = true };
        converter = new MapConverter(BlockNames.Known(dataDir), dataRules, Catalog, Builder);
        dataStamp = stamp;
        Picks = PickCatalog.Load(dataDir);
        Groups = GroupCatalog.Load(dataDir);
        return true;
    }

    public Plan Run(string mapPath, MapSettings settings)
    {
        SetUpIfDataChanged();
        var map = Gbx.ParseNode<CGameCtnChallenge>(mapPath);
        var problems = new List<string>();
        var own = settings.Read(problems);
        var rulesNow = dataRules!.For(map.MapName, own);
        var defaults = dataRules.For(map.MapName, new ConversionRules()).Options;
        var before = Before.Of(map);
        var cameras = ForcedCameras.Of(map);
        var run = converter!.Run(map, own);
        var cameraDecisions = cameras.ToDictionary(c => c.Index, c => CameraDecision(c, rulesNow, Groups));
        var blockDecisions = before.Blocks.Index().ToDictionary(b => b.Index, b => BlockDecision(run, before, b.Index, rulesNow, Groups) is var d ? d with { Cannot = CannotOf(run, b.Item, d.Kind) } : null!);
        var now = new HashSet<object>(map.AnchoredObjects ?? [], ReferenceEqualityComparer.Instance);
        var itemDecisions = before.Items.Index().ToDictionary(o => o.Index, o => ItemDecision(run, o.Item, now, rulesNow, Groups) is var d ? d with { Cannot = CannotOf(run, o.Item.Item, d.Kind) } : null!);
        var keys = before.Keys.Concat(before.PlainKeys).Concat(before.Items.Select(o => ItemKey(o.Id, o.Position))).Concat(cameras.Select(c => c.Key)).Select(Normal).ToHashSet();
        var stale = rulesNow.Pieces.Keys.Where(k => !keys.Contains(k)).Order(StringComparer.Ordinal).ToList();
        return new Plan(OptionList(rulesNow.Options, defaults), blockDecisions, itemDecisions, cameraDecisions, problems, stale, map, run, before, cameras);
    }

    private List<string>? CannotOf(ConversionRun run, object piece, string kind)
        => run.CannotPick.TryGetValue(piece, out var no) && Picks.Kinds.TryGetValue(kind, out var k) && k.Picks.Where(no.Contains).ToList() is { Count: > 0 } list ? list : null;

    private static string KindOf(ConversionRun run, string name, int flags, bool pillarOrFree, string? rule)
    {
        var ground = (flags & BlockFlags.Ground) != 0;
        if (run.Shapes.IsTerrain(name)) return System.Text.RegularExpressions.Regex.IsMatch(name, "Lake|Sea|Water|River") ? "shore" : "terrain";
        // gate waypoints keep their trigger only native: snow under them on the ground, else nothing
        if (name.StartsWith("Gate") && run.Shapes.Waypoint(name) is not null) return ground ? "gateground" : "";
        if (run.Shapes.Waypoint(name) is not null) return "waypoints";
        if (BlockNames.IsGameplayBlock(name)) return (BlockNames.IsReactor(name) ? "reactor" : "special") + (ground ? "ground" : "");
        var scenery = BlockRules.IsScenery(name, flags, run) || rule is not null && BlockRules.SceneryRules.Contains(rule);
        if (!scenery) return "roads";
        return pillarOrFree || run.Shapes.IsPillarBlock(name) ? "decononsnow" : "deco";
    }

    private static string Inv(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string KeyOf(string name, CGameCtnBlock block, Dictionary<CGameCtnBlock, string> ambiguous)
        => ambiguous.GetValueOrDefault(block) is { } own ? own
            : block.IsFree && block.AbsolutePositionInMap is { } p ? $"{name} {Inv(p.X)}, {Inv(p.Y)}, {Inv(p.Z)}" : ConversionRules.KeepKey(name, block.Coord.X, block.Coord.Y, block.Coord.Z);

    // the map as it was: the run changes it in place
    public sealed record Before(List<CGameCtnBlock> Blocks, List<string> Names, List<string> Keys, List<string> PlainKeys, List<int> Flags, List<bool> PillarOrFree,
        List<(Direction, Vec3?, Vec3?)> Placements, List<(CGameCtnAnchoredObject Item, string Id, Vec3 Position)> Items)
    {
        public static Before Of(CGameCtnChallenge map)
        {
            var blocks = map.GetBlocks().ToList();
            var ambiguous = PieceKeys.Ambiguous(map);
            return new(blocks,
                blocks.Select(b => b.Name).ToList(),
                blocks.Select(b => KeyOf(b.Name, b, ambiguous)).ToList(),
                blocks.Select(b => KeyOf(b.Name, b, [])).ToList(),
                blocks.Select(b => b.Flags).ToList(),
                blocks.Select(b => b.IsPillar || b.IsFree).ToList(),
                blocks.Select(b => (b.Direction, b.AbsolutePositionInMap, b.YawPitchRoll)).ToList(),
                (map.AnchoredObjects ?? []).Select(o => (Item: o, o.ItemModel.Id, o.AbsolutePositionInMap)).ToList());
        }
    }

    private static Decision BlockDecision(ConversionRun run, Before before, int i, ConversionRules rulesNow, GroupCatalog groups)
    {
        var d = Of(run, before.Blocks[i]);
        var (name, kind) = (before.Names[i], KindOf(run, before.Names[i], before.Flags[i], before.PillarOrFree[i], d.Rule));
        // no kind: nothing to pick
        if (run.DataRules.Unsupported(name)) return d with { Key = before.Keys[i], Group = groups.UnsupportedPath };
        return d with
        {
            Kind = kind,
            Group = groups.PathOf(kind, name, run.Shapes.Waypoint(name), tree: false, ground: CoverFill.AtGroundLevel(before.Blocks[i], run.Collection)),
            Key = before.Keys[i],
            Sides = HasSides(run, name, before.Flags[i]),
            // a shared block picked by its cell's key before keys told them apart
            Picked = rulesNow.Pieces.GetValueOrDefault(Normal(before.Keys[i])) ?? rulesNow.Pieces.GetValueOrDefault(Normal(before.PlainKeys[i])),
        };
    }

    private static bool HasSides(ConversionRun run, string name, int flags)
        => run.Shapes.Materials(name, (flags & BlockFlags.Ground) != 0, (flags >> 21) & 0x3F, flags & 0xFFF, ConversionRun.NoClips) is { } materials
            && materials.Any(run.Look.Theme.SideMaterials.Contains);

    private static Decision ItemDecision(ConversionRun run, (CGameCtnAnchoredObject Item, string Id, Vec3 Position) was, HashSet<object> now, ConversionRules rulesNow, GroupCatalog groups)
    {
        var (item, id, position) = was;
        Decision d = !now.Contains(item) ? new("removed", null, null)
            : item.ItemModel.Id != id ? new(Paths.IsOurs(item.ItemModel.Id) ? "ice" : "swapped", null, item.ItemModel.Id)
            : item.AbsolutePositionInMap != position ? new("sunk", null, null)
            : new("unchanged", null, null);
        var itemKey = ItemKey(id, position);
        // no kind: nothing to pick
        if (run.DataRules.Unsupported(id)) return d with { Key = itemKey, Group = groups.UnsupportedPath };
        // gate waypoints: an ice copy has no trigger
        if (run.Shapes.ItemWaypoint(id) is { } waypoint) return d with { Key = itemKey, Group = groups.PathOf("", id, waypoint, tree: false) };
        return d with
        {
            Kind = "items",
            Key = itemKey,
            Picked = rulesNow.Pieces.GetValueOrDefault(Normal(itemKey)),
            Group = groups.PathOf("items", id, run.Shapes.ItemWaypoint(id), tree: ItemSwaps.CoverSwap(run, id) is not null),
        };
    }

    private static Decision CameraDecision(ForcedCameras.Camera camera, ConversionRules rulesNow, GroupCatalog groups)
        => rulesNow.Pieces.GetValueOrDefault(camera.Key) is var picked && (picked == "disable" ? new Decision("disabled", "Picked", null) : new Decision("unchanged", null, null)) is var d
            ? d with { Kind = "camera", Key = camera.Key, Picked = picked, Group = groups.PathOf("camera", camera.Name, null, tree: false) }
            : null!;

    // every piece's target key by index in the map's own order, before any run: the page marks what the next run is working on
    public sealed record KeyList(List<string> Blocks, List<string> PlainBlocks, List<string> Items);

    public static KeyList KeysOf(string mapPath)
    {
        var before = Before.Of(Gbx.ParseNode<CGameCtnChallenge>(mapPath));
        return new([.. before.Keys.Select(Normal)], [.. before.PlainKeys.Select(Normal)], [.. before.Items.Select(o => Normal(ItemKey(o.Id, o.Position)))]);
    }

    private static string ItemKey(string id, Vec3 position) => $"{id} {Inv(position.X)}, {Inv(position.Y)}, {Inv(position.Z)}";

    private static string Normal(string key) => System.Text.RegularExpressions.Regex.Replace(key.Trim(), @"\s*,\s*", ",");

    private static Decision Of(ConversionRun run, CGameCtnBlock block)
    {
        var rule = run.DecidedBy.GetValueOrDefault(block);
        var custom = run.Customs.GetValueOrDefault(block);
        if (run.Skinned.Contains(block)) return new("skin", rule, null);
        if (run.Replaced.Contains(block)) return new("item", rule, null);
        if (run.EngineOffGates.Contains(block)) return new("removed", rule, null);
        // native in the air, its ground a custom block on the cell below
        if (run.CoveredGameplay?.Values.Any(g => g.Block == block) == true) return new("custom", rule, null);
        if (run.Renamed.Contains(block)) return new("twin", rule, block.Name);
        // tops iced into a custom block are still a tops-only change
        if (run.Topped.Contains(block)) return new("tops", rule, custom?.Ident.Id);
        if (custom is not null) return new("custom", rule, custom.Ident.Id);
        if (run.SceneryKept.Contains(block)) return new("kept", rule, null);
        return new("unchanged", rule, null);
    }

    private static List<Option> OptionList(ConvertOptions options, ConvertOptions mapOptions)
    {
        var map = mapOptions.All().ToDictionary(o => o.Name, o => o.Value);
        var described = ConvertOptions.Described().ToDictionary(o => o.Name);
        return [.. options.All().Select(o => new Option(o.Name, o.Value, map[o.Name], described[o.Name].Category, described[o.Name].Description))];
    }

    // defaults alone, as the page lists them before a map's run
    public List<Option> DefaultOptions()
    {
        var defaults = ConversionRules.Load(dataDir).For("", new ConversionRules()).Options;
        return OptionList(defaults, defaults);
    }
}
