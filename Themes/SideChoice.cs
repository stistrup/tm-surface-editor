namespace TmSurfaceEditor;

// data/sides.jsonc: what a block's sides become, by role ("@sides", "@cliff", "@skin" in a theme's materials.jsonc)
public sealed record SideChoice(string Label, string Sides, string Cliff, string Skin)
{
    private static readonly Dictionary<string, Dictionary<string, SideChoice>> loaded = [];

    public static Dictionary<string, SideChoice> All(string dataDir)
    {
        var file = Path.GetFullPath(Path.Combine(dataDir, "sides.jsonc"));
        lock (loaded) return loaded.TryGetValue(file, out var known) ? known : loaded[file] = Jsonc.Load<Dictionary<string, SideChoice>>(file);
    }

    public static SideChoice Of(string dataDir, string name)
        => All(dataDir).TryGetValue(name, out var side) ? side : throw new FormatException($"sides.jsonc: no sides '{name}'");

    public string Role(string role) => role switch
    {
        "sides" => Sides,
        "cliff" => Cliff,
        "skin" => Skin,
        _ => throw new FormatException($"no sides role '@{role}'"),
    };

    // library materials it brings in: they may drive as a surface the theme replaces
    public HashSet<string> Materials => [.. new[] { Sides, Cliff, Skin }.Where(m => m is not ("" or "native"))];
}
