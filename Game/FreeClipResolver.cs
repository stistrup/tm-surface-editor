using System.Numerics;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// free blocks connect like grid blocks where they line up (same up, quarter turns, offsets on the 32/8 grid);
// each lined-up set is resolved on its own grid, in the frame of its first block
public static class FreeClipResolver
{
    public sealed record Frame(Matrix4x4 ToWorld, List<int> Members);

    public static Matrix4x4 PlaceOf(CGameCtnBlock block)
    {
        var (p, r) = (block.AbsolutePositionInMap ?? default, block.YawPitchRoll ?? default);
        return Matrix4x4.CreateFromYawPitchRoll(r.X, r.Y, r.Z) * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
    }

    public static List<Frame> Frames(IReadOnlyList<CGameCtnBlock> free)
    {
        var places = free.Select(PlaceOf).ToList();
        var taken = new bool[free.Count];
        var frames = new List<Frame>();
        for (int i = 0; i < free.Count; i++)
        {
            if (taken[i]) continue;
            Matrix4x4.Invert(places[i], out var toFrame);
            var members = new List<int> { i };
            for (int j = i + 1; j < free.Count; j++)
                if (!taken[j] && OnGrid(places[j] * toFrame)) { members.Add(j); taken[j] = true; }
            frames.Add(new Frame(places[i], members));
        }
        return frames;
    }

    // shown clips per free block (all clips of a block nothing lines up with)
    public static List<HashSet<int>> Shown(BlockCatalog shapes, IReadOnlyList<CGameCtnBlock> free)
    {
        var shown = free.Select(b => Drawn(shapes, b)).ToList();
        var resolver = new ClipResolver(shapes);
        foreach (var frame in Frames(free).Where(f => f.Members.Count > 1))
        {
            Matrix4x4.Invert(frame.ToWorld, out var toFrame);
            var placed = frame.Members.Select(i => new ClipResolver.Placed(free[i].Name, false, BlockFlags.VariantOf(free[i]),
                PlaceOf(free[i]) * toFrame)).ToList();
            var resolved = resolver.Resolve(placed);
            for (int k = 0; k < frame.Members.Count; k++) shown[frame.Members[k]] = resolved[k];
        }
        return shown;
    }

    public static HashSet<int> Drawn(BlockCatalog shapes, CGameCtnBlock block)
        => shapes.ClipSides(block.Name, false, BlockFlags.VariantOf(block))
            .SelectMany(s => s.Clips.Where(c => !c.Anti).Select(c => ClipKey.Of(s.Bit, c.Index))).ToHashSet();

    private static bool OnGrid(Matrix4x4 m)
    {
        static bool Unit(float v) => MathF.Abs(v) < 1e-3f || MathF.Abs(MathF.Abs(v) - 1) < 1e-3f;
        static bool Step(float v, float size) => MathF.Abs(v / size - MathF.Round(v / size)) * size < 0.01f;
        return MathF.Abs(m.M22 - 1) < 1e-3f && Unit(m.M11) && Unit(m.M13) && Unit(m.M31) && Unit(m.M33)
            && Step(m.M41, 32) && Step(m.M42, 8) && Step(m.M43, 32);
    }
}
