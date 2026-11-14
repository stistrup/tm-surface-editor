using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;
using static TmSurfaceEditor.BlockFlags;
using static TmSurfaceEditor.Counts;

namespace TmSurfaceEditor;

// per-map snowTerrain areas
internal sealed class CoverArea
{
    // cells where a ground block hides the terrain; air blocks and pillars leave it showing
    public HashSet<Int3> BuiltOn { get; }
    public HashSet<(int X, int Z)> UnderDeco { get; }

    public CoverArea(ConversionRun run)
    {
        var (shapes, rules) = (run.Shapes, run.Rules);
        UnderDeco = run.Grid.Where(b => !b.IsGround && b.Name.StartsWith("Deco") && rules.CoversTerrainAt(b.Coord.X, b.Coord.Z) && run.BelowGround(b)).SelectMany(b =>
        {
            var size = shapes.BlockSize(b.Name, false, VariantOf(b));
            var (sx, sz) = b.Direction is Direction.East or Direction.West ? (size.Z, size.X) : (size.X, size.Z);
            // one cell can't reach over another
            return sx * sz == 1 ? [] : Enumerable.Range(0, sx).SelectMany(dx => Enumerable.Range(0, sz).Select(dz => (b.Coord.X + dx, b.Coord.Z + dz)));
        }).ToHashSet();
        BuiltOn = run.Grid.Where(b => b.IsGround && !b.IsPillar && !shapes.IsTerrain(b.Name) && !shapes.IsPillarBlock(b.Name)).SelectMany(b => run.Cells[b]).ToHashSet();
    }
}

internal static class CoverRules
{
    // open water; shores (WaterHill, LakeShore) are ground at the water's edge
    private static bool IsWater(string terrain) => HasWater(terrain) && !IsBank(terrain);

    public static bool IsShore(string terrain) => HasWater(terrain) && IsBank(terrain);

    private static bool HasWater(string terrain) => Regex.IsMatch(terrain, "Lake|Sea|Water|River");

    private static bool IsBank(string terrain) => Regex.IsMatch(terrain, "Hill|Shore");

    // snow terrain there: in a snow area, land, not built on
    public static bool CoversCell(ConversionRun run, int x, int z)
        => run.Rules.CoversTerrainAt(x, z) && run.Grid.Any(t => t.Coord.X == x && t.Coord.Z == z && run.Shapes.IsTerrain(t.Name) && !IsWater(t.Name)
            && !run.Cover.BuiltOn.Any(b => b.X == x && b.Z == z && b.Y <= t.Coord.Y + 1));

    // the native block goes, the game still draws the ground; placed as variant 0 of the archetype
    public static bool TerrainCell(CGameCtnBlock block, ConversionRun run)
    {
        var (name, coord) = (block.Name, block.Coord);
        if (!run.Shapes.IsTerrain(name) || !run.Rules.CoversTerrainAt(coord.X, coord.Z) || IsWater(name)
            || run.Cover.BuiltOn.Any(c => c.X == coord.X && c.Z == coord.Z && c.Y <= coord.Y + 1)
            || run.Items.CoverTerrainFor(name, VariantOf(block), MobilOf(block), run.Map.Collection!.Value, run.Cover.UnderDeco.Contains((coord.X, coord.Z))) is not { } snowBlock) return false;
        run.Covered.Add((block, VariantOf(block), MobilOf(block)));
        Cover(run, block, snowBlock);
        block.Flags &= ~0xFFF & ~(0x3F << 21);
        Bump(run.Report.CoveredTerrain, name);
        return true;
    }

    // a ground pillar's bottom clip lays a full-cell Deco patch (with its vegetation) level with the snow; the air pillar has none
    public static bool PillarOnCover(CGameCtnBlock block, ConversionRun run)
    {
        var coord = block.Coord;
        if (block.IsGround && (block.IsPillar || run.Shapes.IsPillarBlock(block.Name)) && run.Rules.CoversTerrainAt(coord.X, coord.Z)
            && run.Grid.Any(t => t.Coord.X == coord.X && t.Coord.Z == coord.Z && run.Shapes.IsTerrain(t.Name) && !IsWater(t.Name)))
            block.Flags &= ~BlockFlags.Ground;
        return false;
    }

    // snow picked on a non-terrain block: its deco surface in the snow ground
    public static bool PickedCover(CGameCtnBlock block, ConversionRun run)
    {
        // a custom block loses the native trigger: the block goes to the air, its ground onto the terrain cell
        if (block.IsGround && !block.IsFree && BlockRules.PrefabTrigger(block.Name, run.Shapes))
        {
            if (!CoveredGameplay(run).Values.Any(g => g.Block == block)) return false;
            // its terrain-bringing ground variant would still lay its dirt: the plain air variant
            block.Flags &= ~BlockFlags.Ground & ~BlockFlags.Replacement & ~(0x3F << 21);
            Bump(run.Report.CoveredTerrain, block.Name);
            return true;
        }
        if (run.Shapes.IsTerrain(block.Name) || block.IsPillar || run.Shapes.IsPillarBlock(block.Name) || block.IsFree
            || run.Items.CoverDecoFor(block.Name, VariantOf(block), MobilOf(block), run.Map.Collection!.Value, block.IsGround) is not { } snowDeco) return false;
        Cover(run, block, snowDeco);
        Bump(run.Report.CoveredTerrain, block.Name);
        return true;
    }

    // the terrain cell under a gameplay block picked to snow carries that block's ground
    public static bool UnderCoveredGameplay(CGameCtnBlock block, ConversionRun run)
    {
        var coord = block.Coord;
        if (!run.Shapes.IsTerrain(block.Name) || IsWater(block.Name) || !CoveredGameplay(run).TryGetValue((coord.X, coord.Z), out var above)
            || run.Items.GameplayGroundFor(above.Block.Name, above.Variant, above.Mobil, run.Map.Collection!.Value) is not { } ground) return false;
        Cover(run, block, ground);
        run.GroundOf[block] = above.Block;
        (block.Coord, block.Direction) = (above.Block.Coord, above.Block.Direction);
        block.Flags &= ~0xFFF & ~(0x3F << 21);
        return true;
    }

    private static void Cover(ConversionRun run, CGameCtnBlock block, BuiltBlock body)
    {
        run.Customs[block] = body;
        run.CoverBodies.Add(block);
    }

    // a terrain block (not water) in its column carries the snow ground; base ground alone has none
    public static bool TerrainUnder(CGameCtnBlock block, ConversionRun run)
        => run.Grid.Any(t => t.Coord.X == block.Coord.X && t.Coord.Z == block.Coord.Z && run.Shapes.IsTerrain(t.Name) && !IsWater(t.Name));

    // ground gameplay blocks picked to snow, by cell, with terrain under them, and their ground variant; read before any of them goes to the air
    private static Dictionary<(int X, int Z), (CGameCtnBlock Block, int Variant, int Mobil)> CoveredGameplay(ConversionRun run)
        => run.CoveredGameplay ??= run.Grid.Where(b => b.IsGround && !b.IsFree && BlockRules.PrefabTrigger(b.Name, run.Shapes) && run.Rules.PieceChoice(b) == "snow" && TerrainUnder(b, run))
            .GroupBy(b => (b.Coord.X, b.Coord.Z)).ToDictionary(g => g.Key, g => (g.First(), VariantOf(g.First()), MobilOf(g.First())));

    public static bool DecoInTerrain(CGameCtnBlock block, ConversionRun run)
    {
        if (!run.InTerrain(block) || !run.Rules.CoversTerrainAt(block.Coord.X, block.Coord.Z)
            || run.Items.CoverDecoFor(block.Name, VariantOf(block), MobilOf(block), run.Map.Collection!.Value) is not { } snowDeco) return false;
        Cover(run, block, snowDeco);
        Bump(run.Report.CoveredTerrain, block.Name);
        return true;
    }
}
