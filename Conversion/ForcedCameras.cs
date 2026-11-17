using System.Numerics;
using GBX.NET;
using GBX.NET.Engines.Game;

namespace TmSurfaceEditor;

// in-game clips that force a camera change, or end one (an empty clip stops the playing one): one piece each, picked "disable" to take it out
internal static class ForcedCameras
{
    // trigger cells: a third of a block across, one block level high
    public static readonly Vector3 Cell = new(32f / 3, 8, 32f / 3);

    // Label: the game camera it switches to, as the game names it
    public sealed record Camera(int Index, string Name, string Key, string Label, List<Int3> Cells);

    public static List<Camera> Of(CGameCtnChallenge map)
        => [.. (map.ClipGroupInGame?.Clips ?? []).Index()
            .Where(c => c.Item.Clip.Tracks.Count == 0 || c.Item.Clip.Tracks.Any(IsCamera))
            .Select(c => new Camera(c.Index, c.Item.Clip.Name ?? "", KeyOf(c.Index, c.Item.Clip.Name),
                LabelOf(c.Item.Clip.Tracks.SelectMany(t => t.Blocks).OfType<CGameCtnMediaBlockCameraGame>().FirstOrDefault()?.GameCam), [.. c.Item.Trigger.Coords]))];

    // Internal is Cam 3 (Fall 2026 18's "Cam 3" clip); External / External_2 as Cam 1 / Cam 2 not seen in a map yet;
    // no camera (an empty clip) or Default: back to the player's own camera
    private static string LabelOf(CGameCtnMediaBlockCameraGame.EGameCam? cam) => cam switch
    {
        CGameCtnMediaBlockCameraGame.EGameCam.External => "Cam 1",
        CGameCtnMediaBlockCameraGame.EGameCam.External_2 => "Cam 2",
        CGameCtnMediaBlockCameraGame.EGameCam.Internal => "Cam 3",
        CGameCtnMediaBlockCameraGame.EGameCam.Helico => "Helico cam",
        CGameCtnMediaBlockCameraGame.EGameCam.Free => "Free cam",
        CGameCtnMediaBlockCameraGame.EGameCam.Spectator => "Spectator cam",
        _ => "Cam reset",
    };

    public static string KeyOf(int index, string? name) => ConversionRules.NormalizeKeep($"Camera {index} {name}");

    // a trigger cell in the scene's space: levels as block coords (Placement.Of), the environment drop applied
    public static Vector3 Corner(Int3 cell, string? collection)
        => new Vector3(cell.X * Cell.X, (cell.Y - 8) * Cell.Y - Placement.EnvironmentDrop(collection), cell.Z * Cell.Z);

    // cells that touch by a face, together
    public static List<List<Int3>> Areas(List<Int3> cells)
    {
        var left = cells.ToHashSet();
        var areas = new List<List<Int3>>();
        foreach (var start in cells)
        {
            if (!left.Remove(start)) continue;
            var area = new List<Int3> { start };
            for (int i = 0; i < area.Count; i++)
                foreach (var d in new Int3[] { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) })
                    if (left.Remove(area[i] + d)) area.Add(area[i] + d);
            areas.Add(area);
        }
        return areas;
    }

    public static void Disable(ConversionRun run)
    {
        if (run.Map.ClipGroupInGame?.Clips is not { } clips) return;
        var picked = Of(run.Map).Where(c => run.Rules.Pieces.GetValueOrDefault(c.Key) == "disable").Select(c => clips[c.Index]).ToList();
        foreach (var c in picked)
        {
            c.Clip.Tracks.RemoveAll(IsCamera);
            run.Report.CameraClipsRemoved.Add(c.Clip.Name ?? "(unnamed)");
        }
        clips.RemoveAll(c => picked.Contains(c) && c.Clip.Tracks.Count == 0);
    }

    private static bool IsCamera(CGameCtnMediaTrack track) => track.Blocks.Any(b => b is CGameCtnMediaBlockCameraGame);
}
