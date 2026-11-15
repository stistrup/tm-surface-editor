using System.Text.Json.Serialization;

namespace TmSurfaceEditor;

// what roads turn into and what covers the ground: data/themes/<name>/
public sealed class Theme
{
    public const string DefaultName = "ice";

    public string Tag { get; init; } = "";
    public HashSet<string> Surfaces { get; init; } = [];
    public HashSet<string> Replaces { get; init; } = [];
    public string Road { get; init; } = "";
    public string Cover { get; init; } = "";
    public string Family { get; init; } = "";
    public string PadSurface { get; init; } = "";
    public string? BlockWord { get; init; }
    // data/sides.jsonc: what its sides become unless a pick says otherwise
    public string Sides { get; init; } = "original";
    // the road texture's grain or streaks: texture "u" or "v" laid along the road
    public string RoadAlong { get; init; } = "u";

    [JsonIgnore]
    public string Name { get; private set; } = "";

    [JsonIgnore]
    public string Folder { get; private set; } = "";

    [JsonIgnore]
    private string DataDir { get; set; } = "";

    public string MaterialsFile => Path.Combine(Folder, "materials.jsonc");

    public IEnumerable<string> Files => [Path.Combine(Folder, "theme.jsonc"), MaterialsFile];

    // file names start with it: recognisable in the editor and the items folder
    public string ItemPrefix => "g" + Tag + "_";

    public string NameTag => "_" + Tag;

    private static readonly Dictionary<string, Theme> loaded = [];

    public static Theme Default => Load(Paths.Data);

    public static Theme Load(string dataDir, string name = DefaultName)
    {
        var folder = Path.GetFullPath(Path.Combine(dataDir, "themes", name));
        lock (loaded)
        {
            if (loaded.TryGetValue(folder, out var known)) return known;
            var theme = Jsonc.Load<Theme>(Path.Combine(folder, "theme.jsonc"));
            theme.Folder = folder;
            theme.Name = name;
            theme.DataDir = dataDir;
            return loaded[folder] = theme;
        }
    }

    public static IEnumerable<Theme> All(string dataDir)
        => Directory.EnumerateDirectories(Path.Combine(dataDir, "themes")).Order()
            .Where(d => File.Exists(Path.Combine(d, "theme.jsonc"))).Select(d => Load(dataDir, Path.GetFileName(d)));

    // the theme an item or block of ours was built in, by its prefix; names from before the prefix: the default
    public static Theme ForItem(string name) => All(Paths.Data).FirstOrDefault(t => name.StartsWith(t.ItemPrefix)) ?? Default;

    public static bool Exists(string dataDir, string name) => File.Exists(Path.Combine(dataDir, "themes", name, "theme.jsonc"));

    // every theme's files: items are stamped with all of them
    public static IEnumerable<string> AllFiles(string dataDir)
        => Directory.EnumerateFiles(Path.Combine(dataDir, "themes"), "*.jsonc", SearchOption.AllDirectories).Order();

    // game materials that are sides ("@<role>" in materials.jsonc)
    [JsonIgnore]
    public HashSet<string> SideMaterials => sideMaterials ??= [.. Jsonc.Load<Dictionary<string, string>>(MaterialsFile)
        .Where(kv => kv.Value.StartsWith('@') && !kv.Key.Contains(':')).Select(kv => kv.Key)];
    private HashSet<string>? sideMaterials;

    public SideChoice SidesOf(string? sides) => SideChoice.Of(DataDir, sides ?? Sides);

    // a fresh copy each time; "item:" entries only on item copies, "top:" only on top skins; "@<role>" by the sides
    public Dictionary<string, string> Map(string? prefix, string? sides = null)
    {
        var map = Jsonc.Load<Dictionary<string, string>>(MaterialsFile);
        var side = SidesOf(sides);
        foreach (var (key, to) in map.Where(kv => kv.Value.StartsWith('@')).ToList()) map[key] = side.Role(to[1..]);
        if (prefix is not null)
            foreach (var (key, to) in map.Where(kv => kv.Key.StartsWith(prefix)).ToList()) map[key[prefix.Length..]] = to;
        return map;
    }

    // a game material the theme turns into its road, or one that already drives as the theme
    public bool IsRoadMaterial(string material) => RoadMaterials.Contains(material) || DrivesAsTheme(material);

    [JsonIgnore]
    private HashSet<string> RoadMaterials => roadMaterials ??= [.. Map(null).Where(kv => DrivesLikeRoad(kv.Value) && !kv.Key.Contains(':') && !kv.Key.Contains('|')).Select(kv => kv.Key)];
    private HashSet<string>? roadMaterials;

    // the road itself or a library material on its surface (bordered roads: RoadIce)
    public bool DrivesLikeRoad(string material) => material == Road || MaterialLib.Surface(material) is { } s && s == MaterialLib.Surface(Road);

    public bool DrivesAsTheme(string material) => MaterialLib.Surface(material) is { } s && Surfaces.Contains(s);

    public bool SwappedIn(IReadOnlyDictionary<string, string> map, string material)
        => map.TryGetValue(material, out var to) && to != material && DrivesAsTheme(to);

    // a library material on a surface the theme leaves alone
    public bool KeepsOwnSurface(string material) => MaterialLib.Surface(material) is { } s && !Replaces.Contains(s);

    // Deco only on deco blocks (or with snowOnZoneSides)
    public bool SkinCovers(string block, string material, bool zoneSides = false)
        => material.StartsWith("Deco") && !(zoneSides && MaterialLib.Surface(material) is { } deco && SkinsOver(deco))
            ? block.StartsWith("Deco")
            : MaterialLib.Surface(material) is { } surface && SkinsOver(surface);

    private bool SkinsOver(string surface) => Replaces.Contains(surface) || surface == "RoadSynthetic";
}
