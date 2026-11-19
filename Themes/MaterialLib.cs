using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Plug;

namespace TmSurfaceEditor;

// NadeoImporterMaterialLib.txt and the game's own materials
internal static class MaterialLib
{
    public static string Text => Lib.Value;

    public static bool LightmapOnly(string name)
    {
        var m = Regex.Match(Text, $@"DMaterial\({Regex.Escape(name)}\)(.*?)(?=DMaterial\(|$)", RegexOptions.Singleline);
        return m.Success && Regex.IsMatch(m.Groups[1].Value, @"DUvLayer\s*\(\s*Lightmap\s*,\s*0\s*\)");
    }

    // a material that links a game material drives as that one (the library has PlatformPlastic_PlatformTech as RoadIce)
    public static string? Surface(string name)
        => surfaces.GetOrAdd(name, n => Regex.Match(Text, $@"DMaterial\({Regex.Escape(n)}\)((?:(?!DMaterial\().)*)", RegexOptions.Singleline) is { Success: true } m
            ? LinkedSurface(m.Groups[1].Value) ?? (Regex.Match(m.Groups[1].Value, @"DSurfaceId\s*\((\w+)\)") is { Success: true } s ? s.Groups[1].Value : null)
            : null);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> surfaces = new();

    private static string? LinkedSurface(string entry)
        => Regex.Match(entry, @"DLinkFull\s*\(([^)]*)\)") is { Success: true } link
            && GameFiles.OnDisk(Path.Combine(Paths.ExtractedStadium, link.Groups[1].Value.Trim().Replace('\\', '/') + ".Material.Gbx")) is var file && File.Exists(file)
            ? Gbx.ParseNode<CPlugMaterial>(file).SurfaceId.ToString() : null;

    private static readonly Lazy<string> Lib = new(() => File.ReadAllText(Path.Combine(Paths.Game, "NadeoImporterMaterialLib.txt")));

    // kept terrain the importer can't take: built on a library stand-in with the same surface, then linked to the game's own material
    public const string NativePrefix = "Native_";

    // a library material with texture and lightmap UVs, as the game's materials expect (Custom* are lightmap-only: the game crashed),
    // one per surface and none the item uses itself, or the importer merges their collision
    public static Dictionary<string, string> NativeStandIns(List<string> materials, Dictionary<string, string> physics)
    {
        var candidates = Regex.Matches(Text, @"DMaterial\((\w+)\)((?:(?!DMaterial\().)*)", RegexOptions.Singleline)
            .Where(m => Regex.IsMatch(m.Groups[2].Value, @"DUvLayer\s*\(\s*BaseMaterial\s*,\s*0\s*\)") && Regex.IsMatch(m.Groups[2].Value, @"DUvLayer\s*\(\s*Lightmap\s*,\s*1\s*\)"))
            .Select(m => (Name: m.Groups[1].Value, Surface: Regex.Match(m.Groups[2].Value, @"DSurfaceId\s*\(\s*(\w+)").Groups[1].Value))
            // water stand-ins turned the block's terrain white
            .Where(c => !materials.Contains(c.Name)).OrderBy(c => c.Name.Contains("Water")).ToList();
        var result = new Dictionary<string, string>();
        foreach (var surface in materials.Where(m => m.StartsWith(NativePrefix)).Select(m => physics[m]).Distinct().Order())
        {
            var pick = candidates.FirstOrDefault(c => c.Surface == surface && !result.ContainsValue(c.Name)).Name
                ?? candidates.First(c => !result.ContainsValue(c.Name)).Name;
            result[surface] = pick;
        }
        return result;
    }

    // the environment's own version first (vistas restyle Stadium materials)
    public static string? NativeLink(string material, string? environment)
    {
        var root = Path.GetDirectoryName(Paths.ExtractedStadium)!;
        var env = environment is null || environment.StartsWith("Stadium") ? "Stadium" : environment;
        // vistas swap Stadium's walls for "…InWorld" versions
        return new[] { $"{env}\\Media\\Modifier\\StadiumOnTerrain\\{material}", $"{env}\\Media\\Modifier\\StadiumOnTerrain\\{material}InWorld", $"{env}\\Media\\Material\\{material}", $"Stadium\\Media\\Material\\{material}" }
            .FirstOrDefault(l => File.Exists(GameFiles.OnDisk(Path.Combine(root, l.Replace('\\', '/') + ".Material.Gbx"))));
    }

    public static string NativeSurface(string name, string? environment)
        => Gbx.ParseNode<CPlugMaterial>(GameFiles.OnDisk(Path.Combine(Path.GetDirectoryName(Paths.ExtractedStadium)!,
            NativeLink(name[NativePrefix.Length..], environment)!.Replace('\\', '/') + ".Material.Gbx"))).SurfaceId.ToString();

    public static Dictionary<string, string> Surfaces(IEnumerable<string> names)
    {
        return names.ToDictionary(n => n, n => Surface(n)
            ?? throw new InvalidDataException($"material {n} isn't in NadeoImporterMaterialLib.txt; map it in the theme's materials.jsonc"));
    }
}
