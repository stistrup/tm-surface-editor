using System.Numerics;
using System.Text;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// how the game turns a block type's top/bottom clips (per clip slot), in quarter turns on top of the block's direction;
// learned from the clips the game baked into maps
public static class ClipTurns
{
    public static string FilePath => Path.Combine(Paths.Data, "clip-turns.jsonc");

    private static readonly Lazy<Dictionary<string, int>> table = new(() => File.Exists(FilePath)
        ? Jsonc.Load<Dictionary<string, int>>(FilePath) : []);

    // this block's own; else what blocks on the same main prefab agree on (one shape in other surfaces: PlatformIceLoopEnd ~ PlatformTechLoopEnd);
    // else what every other block with this clip in this slot agrees on (two at least): checked against the table itself 313 right / 7 wrong, the fitted turn 438 / 160
    public static int? Learned(string blockName, int bit, string clipName, Func<string, string?>? mainPrefabOf = null)
    {
        if (table.Value.TryGetValue(Key(blockName, bit, clipName), out var turn)) return turn;
        if (!shared.Value.TryGetValue((bit, clipName), out var rows)) return null;
        if (mainPrefabOf?.Invoke(blockName) is { } main
            && rows.Where(r => mainPrefabOf(r.Block) == main).Select(r => r.Turn).Distinct().ToList() is [var same]) return same;
        return rows.Count >= 2 && rows.Select(r => r.Turn).Distinct().Count() == 1 ? rows[0].Turn : null;
    }

    private static readonly Lazy<Dictionary<(int Bit, string Clip), List<(string Block, int Turn)>>> shared = new(() => table.Value
        .Select(kv => (Parts: kv.Key.Split('|'), Turn: kv.Value)).Where(e => e.Parts.Length == 3)
        .GroupBy(e => (int.Parse(e.Parts[1]), e.Parts[2])).ToDictionary(g => g.Key, g => g.Select(e => (e.Parts[0], e.Turn)).ToList()));

    public static string Key(string blockName, int bit, string clipName) => $"{blockName}|{bit}|{clipName}";

    // game direction of a placement (Placement.Of turns by -90° per step)
    public static int DirOf(Matrix4x4 place)
    {
        var x = Vector3.TransformNormal(Vector3.UnitX, place);
        return ((int)MathF.Round(MathF.Atan2(x.Z, x.X) / (MathF.PI / 2)) % 4 + 4) % 4;
    }

    public static Dictionary<string, Dictionary<int, int>> Learn(IEnumerable<CGameCtnChallenge> maps, BlockCatalog shapes)
    {
        var seen = new Dictionary<string, Dictionary<int, int>>();
        foreach (var map in maps)
        {
            shapes.Environment = map.Collection?.ToString() ?? "Stadium2020";
            var grid = map.GetBlocks().Where(b => !b.IsFree).ToList();
            var placed = new ClipResolver(shapes).OfAll(grid);
            var baked = (map.BakedBlocks ?? []).GroupBy(b => (b.Name, new Int3(b.Coord.X, b.Coord.Y, b.Coord.Z))).ToDictionary(g => g.Key, g => g.Select(b => (int)b.Direction).ToList());
            for (int i = 0; i < grid.Count; i++)
                foreach (var side in shapes.ClipSides(placed[i].Name, placed[i].Ground, placed[i].Variant).Where(s => s.Bit % 6 >= 4))
                {
                    var cell = Placement.CellAt(Vector3.Transform(side.Probe, placed[i].Place));
                    // one baked clip of that name in the cell, or it can't be told apart
                    foreach (var clip in side.Clips)
                        if (baked.TryGetValue((clip.Name, cell), out var dirs) && dirs.Count == 1)
                        {
                            var key = Key(grid[i].Name, side.Bit, clip.Name);
                            if (!seen.TryGetValue(key, out var turns)) seen[key] = turns = [];
                            var turn = ((dirs[0] - (int)grid[i].Direction) % 4 + 4) % 4;
                            turns[turn] = turns.GetValueOrDefault(turn) + 1;
                        }
                }
            LearnFree(map, shapes, seen);
        }
        return seen;
    }

    // free blocks: the baked clip in the facing cell (by its centre), turned by the yaw between them; upright blocks only
    private static void LearnFree(CGameCtnChallenge map, BlockCatalog shapes, Dictionary<string, Dictionary<int, int>> seen)
    {
        static bool Upright(CGameCtnBlock b) => b.YawPitchRoll is not { } r || MathF.Abs(r.Y) < 1e-3f && MathF.Abs(r.Z) < 1e-3f;
        static int Quarter(CGameCtnBlock b) => (int)MathF.Round(-(b.YawPitchRoll?.X ?? 0) / (MathF.PI / 2));
        var centre = new Vector3(16, 4, 16);
        var baked = (map.BakedBlocks ?? []).Where(b => b.IsFree && Upright(b))
            .Select(b => (b.Name, At: Vector3.Transform(centre, FreeClipResolver.PlaceOf(b)), Dir: Quarter(b), Yaw: b.YawPitchRoll?.X ?? 0)).ToList();
        foreach (var block in map.GetBlocks().Where(b => b.IsFree && Upright(b)))
        {
            var place = FreeClipResolver.PlaceOf(block);
            var yaw = block.YawPitchRoll?.X ?? 0;
            foreach (var side in shapes.ClipSides(block.Name, false, BlockFlags.VariantOf(block)).Where(s => s.Bit % 6 >= 4))
            {
                var cell = Placement.CellAt(side.Probe);
                var at = Vector3.Transform(new Vector3(cell.X * 32 + 16, (cell.Y - 8) * 8 + 4, cell.Z * 32 + 16), place);
                foreach (var clip in side.Clips)
                {
                    var found = baked.Where(b => b.Name == clip.Name && Vector3.Distance(b.At, at) < 1).ToList();
                    // one, turned by whole quarters from the block
                    if (found is not [var only]) continue;
                    var steps = (only.Yaw - yaw) / (MathF.PI / 2);
                    if (MathF.Abs(steps - MathF.Round(steps)) > 0.01f) continue;
                    var key = Key(block.Name, side.Bit, clip.Name);
                    if (!seen.TryGetValue(key, out var turns)) seen[key] = turns = [];
                    var turn = ((only.Dir - Quarter(block)) % 4 + 4) % 4;
                    turns[turn] = turns.GetValueOrDefault(turn) + 1;
                }
            }
        }
    }

    public static void Write(Dictionary<string, Dictionary<int, int>> seen)
    {
        var text = new StringBuilder("// generated by learn-clip-turns: block|clip slot|clip -> quarter turns of the clip on top of the block's direction\n{\n");
        var rows = seen.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var (key, turns) = rows[i];
            var best = turns.MaxBy(t => t.Value);
            var others = turns.Values.Sum() - best.Value;
            text.Append($"  \"{key}\": {best.Key}{(i < rows.Count - 1 ? "," : "")} // seen {best.Value}{(others > 0 ? $", {others} otherwise" : "")}\n");
        }
        File.WriteAllText(FilePath, text.Append("}\n").ToString());
    }
}
