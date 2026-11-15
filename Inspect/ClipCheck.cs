using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// compares the resolver's free clips with the clips the game baked into the map (name + facing cell)
public static class ClipCheck
{
    public sealed record Clip(string Name, Int3 Cell, string Kind, string Block, Int3 BlockCoord);

    public sealed record Result(List<Clip> GameOnly, List<Clip> OursOnly, Dictionary<string, int> Baked, int Edges, List<Clip> EdgeRowWrong);

    public static Result Compare(CGameCtnChallenge map, BlockCatalog shapes)
    {
        shapes.Environment = map.Collection?.ToString() ?? "Stadium2020";
        var grid = map.GetBlocks().Where(b => !b.IsFree).ToList();
        var resolver = new ClipResolver(shapes);
        var placed = resolver.OfAll(grid);
        var shown = resolver.Resolve(placed);
        var possible = new Dictionary<(string, Int3), Clip>();
        var ours = new HashSet<(string, Int3)>();
        var rows = new Dictionary<(string, Int3, int), int>();
        for (int i = 0; i < grid.Count; i++)
            foreach (var side in shapes.ClipSides(placed[i].Name, placed[i].Ground, placed[i].Variant))
            {
                var cell = Placement.CellAt(Vector3.Transform(side.Probe, placed[i].Place));
                foreach (var c in side.Clips.Where(c => c.Type.StartsWith("FreeClip")))
                {
                    var key = (c.Name, cell);
                    possible.TryAdd(key, new Clip(c.Name, cell, KindOf(side.Bit, c), grid[i].Name, new Int3(grid[i].Coord.X, grid[i].Coord.Y, grid[i].Coord.Z)));
                    if (shown[i].FirstOrDefault(k => (k & 0xFFFF) == ClipKey.Of(side.Bit, c.Index), -1) is not -1 and var k)
                    {
                        ours.Add(key);
                        if (c.EdgeKey is not null) rows[(c.Name, cell, DirOf(Vector3.Transform(side.Probe, placed[i].Place) - Vector3.Transform(side.Center, placed[i].Place)))] = k >> 16;
                    }
                }
            }
        // baked names no block side offers (terrain fill) are left out
        var baked = (map.BakedBlocks ?? []).Select(b => (b.Name, new Int3(b.Coord.X, b.Coord.Y, b.Coord.Z))).Where(possible.ContainsKey).ToHashSet();
        // horizontal clips: the mobil row the game baked
        var edgeChecks = (map.BakedBlocks ?? []).Select(b => (Key: (b.Name, new Int3(b.Coord.X, b.Coord.Y, b.Coord.Z), (int)b.Direction), Row: b.Variant & 0x3F))
            .Where(b => rows.ContainsKey(b.Key)).ToList();
        return new Result(
            baked.Except(ours).Select(k => possible[k]).ToList(),
            ours.Except(baked).Select(k => possible[k]).ToList(),
            baked.GroupBy(k => possible[k].Kind).ToDictionary(g => g.Key, g => g.Count()),
            edgeChecks.Count, edgeChecks.Where(b => rows[b.Key] != b.Row).Select(b => possible[(b.Key.Name, b.Key.Item2)] with { Kind = $"row {rows[b.Key]}, game {b.Row}" }).ToList());
    }

    public static readonly string[] Kinds = ["top", "side", "bottom", "vertical"];

    // North 0, East 1, South 2, West 3 (map directions; north is -z)
    private static int DirOf(Vector3 v) => Math.Abs(v.X) > Math.Abs(v.Z) ? (v.X > 0 ? 1 : 3) : (v.Z > 0 ? 2 : 0);

    private static string KindOf(int bit, ClipInfo clip) => bit % 6 == 5 ? "top" : bit % 6 == 4 ? "bottom" : clip.Vertical ? "vertical" : "side";
}
