using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// empty cells (the environment's default zone, not in the map) get the flat terrain block, unless that zone is water
internal static class CoverFill
{
    public static int Run(CGameCtnChallenge map, string collection, BlockCatalog shapes, ConversionRules rules)
    {
        if (DefaultZones.IsWater(DefaultZones.Of(collection)) || FlatBlock(collection) is not { } flat) return 0;
        var size = map.Size;
        var grid = map.GetBlocks().Where(b => !b.IsFree).ToList();
        var resolver = new ClipResolver(shapes);
        var placed = resolver.OfAll(grid);
        // pillars stand on the ground, CoverRules.PillarOnCover lifts them
        var owned = placed.Where((_, i) => grid[i].IsGround && !grid[i].IsPillar && !shapes.IsPillarBlock(grid[i].Name))
            .SelectMany(resolver.UnitCells).Select(c => (c.X, c.Z)).ToHashSet();
        var y = GroundY(collection);
        var filled = 0;
        for (int x = 0; x < size.X; x++)
            for (int z = 0; z < size.Z; z++)
            {
                if (owned.Contains((x, z))) continue;
                map.PlaceBlock(flat, new Int3(x, y, z), Direction.North, true, 0, 0);
                rules.CoverCells.Add((x, z));
                filled++;
            }
        return filled;
    }

    // a gate picked to snow on base ground: the flat land block under it, which then carries its snow ground (CoverRules.UnderCoveredGameplay)
    public static void UnderPickedGates(CGameCtnChallenge map, string collection, BlockCatalog shapes, ConversionRules rules)
    {
        if (!CanFill(collection) || FlatBlock(collection) is not { } flat) return;
        var grid = map.GetBlocks().Where(b => !b.IsFree).ToList();
        var y = GroundY(collection);
        foreach (var gate in grid.Where(b => b.IsGround && BlockRules.PrefabTrigger(b.Name, shapes) && rules.PieceChoice(b) == "snow").ToList())
            if (!grid.Any(t => t.Coord.X == gate.Coord.X && t.Coord.Z == gate.Coord.Z && shapes.IsTerrain(t.Name)))
                grid.Add(map.PlaceBlock(flat, new Int3(gate.Coord.X, y, gate.Coord.Z), Direction.North, true, 0, 0)!);
    }

    public static bool CanFill(string collection) => !DefaultZones.IsWater(DefaultZones.Of(collection)) && FlatBlock(collection) is not null;

    // on the base ground, not a terrace or raised lake; shores sit a level lower, at the water
    public static bool AtGroundLevel(CGameCtnBlock block, string collection)
        => !block.IsFree && block.Coord.Y <= GroundY(collection) - (CoverRules.IsShore(block.Name) ? 1 : 0);

    // grid Y of the flat ground; ground-level water lies one below
    private static int GroundY(string collection) => 8 + (int)(Placement.EnvironmentDrop(collection) / 8);

    // the environment's one flat land block (Dirt, Grass, Land); Stadium has no terrain blocks
    private static string? FlatBlock(string collection)
        => Paths.EnvironmentBlockInfos(Paths.ExtractedStadium, collection) is { } dir && Path.Combine(dir, "GameCtnBlockInfoFlat") is var flats && Directory.Exists(flats)
            ? Directory.EnumerateFiles(flats, "*.EDFlat.Gbx").Select(f => Path.GetFileName(f)[..^".EDFlat.Gbx".Length])
                .FirstOrDefault(n => !Regex.IsMatch(n, "Lake|Sea|Water|River"))
            : null;
}
