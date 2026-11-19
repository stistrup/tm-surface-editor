namespace TmSurfaceEditor;

// one convert's options, terminal and GUI alike
// Folder: where the maps go (absolute). Name: one map only, null = "<map name> (ICE)".
// Settings: handed over whole (the GUI's working ones); else From (--settings) or the map's saved settings / the defaults, Set over them
public sealed record ConvertChoices(string Folder, string? Name = null, bool FakeAuthorTime = false, bool MeAsAuthor = false, bool RebuildBlocks = false, bool Overwrite = false)
{
    // From: a preset alone, nothing under it
    public const string None = "none";

    public MapSettings? Settings { get; init; }
    // after each default name
    public string Suffix { get; init; } = MapNames.DefaultSuffix;
    public string? From { get; init; }
    // applied on top of the settings (data/presets.jsonc)
    public string? Preset { get; init; }
    public IReadOnlyList<(string, bool)> Set { get; init; } = [];

    public string Kind => string.Join(", ", new[] { "convert", FakeAuthorTime ? "fake author time" : null, MeAsAuthor ? "me as author" : null }.OfType<string>());
}

// Output: absolute
public sealed record ConvertOutcome(string Output, string Name, string RunId, string Report, string From, string Summary, List<string> Problems, List<string> Failures, int MissingFiles);

public sealed record MapResult(string Map, ConvertOutcome? Done, string? Error);
