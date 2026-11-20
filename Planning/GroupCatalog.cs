using System.Text.RegularExpressions;

namespace TmSurfaceEditor;

// data/groups.jsonc
internal sealed record GroupCatalog(Dictionary<string, GroupCatalog.Method> Methods, List<GroupCatalog.Group> Groups)
{
    // Options: the rules run with these; Pick: one target for every piece
    public sealed record Method(string Label, Dictionary<string, bool>? Options, string? Pick);

    public sealed record Toggle(string Label, string From, string To);

    // unsupported: takes the notSupported pieces (swaps.jsonc), and only those; ground: terrain at ground level (CoverFill.AtGroundLevel);
    // onConverted: stands on or in a converted block (CoverSurfaces.Under)
    public sealed record Match(List<string>? Kinds, List<string>? Waypoints, string? Name, bool Trees, bool Unsupported, bool Ground, bool OnConverted);

    public sealed record Group(string Id, string Label, List<string>? Methods, List<Toggle>? Toggles, Match? Match, List<Group>? Children);

    // the page's tree: methods and toggles resolved down to every group
    public sealed record Node(string Path, string Label, List<string> Methods, List<Toggle> Toggles, List<Node> Children);

    public static GroupCatalog Load(string dataDir)
    {
        var catalog = Jsonc.Load<GroupCatalog>(Path.Combine(dataDir, "groups.jsonc"));
        var unknown = catalog.Methods.Values.Select(m => m.Pick).OfType<string>().Where(p => !ConversionRules.PieceChoices.Contains(p))
            .Concat(Walk(catalog.Groups).SelectMany(g => (g.Methods ?? []).Concat((g.Toggles ?? []).SelectMany(t => new[] { t.From, t.To }))).Where(m => !catalog.Methods.ContainsKey(m)))
            .ToList();
        if (unknown.Count > 0) throw new FormatException($"groups.jsonc: unknown pick(s) or method(s) {string.Join(", ", unknown.Distinct())}");
        return catalog;
    }

    private static IEnumerable<Group> Walk(IEnumerable<Group> groups) => groups.SelectMany(g => Walk(g.Children ?? []).Prepend(g));

    public List<Node> Tree => Nodes(Groups, "", [], []);

    private static List<Node> Nodes(List<Group> groups, string parent, List<string> methods, List<Toggle> toggles)
        => [.. groups.Select(g =>
        {
            var path = parent == "" ? g.Id : $"{parent}/{g.Id}";
            var (own, ownToggles) = (g.Methods ?? methods, g.Toggles ?? toggles);
            return new Node(path, g.Label, own, ownToggles, Nodes(g.Children ?? [], path, own, ownToggles));
        })];

    public string? UnsupportedPath => Paths(Groups, "").FirstOrDefault(p => p.Group.Match?.Unsupported == true).Path;

    private static IEnumerable<(string Path, Group Group)> Paths(List<Group> groups, string parent)
        => groups.SelectMany(g => (parent == "" ? g.Id : $"{parent}/{g.Id}") is var path ? Paths(g.Children ?? [], path).Prepend((path, g)) : []);

    // the deepest group that takes the piece; null: no group
    public string? PathOf(string kind, string name, string? waypoint, bool tree, bool ground = false, bool onConverted = false)
        => Find(Groups, "", new Piece(kind, name, waypoint, tree, ground, onConverted));

    private sealed record Piece(string Kind, string Name, string? Waypoint, bool Tree, bool Ground, bool OnConverted);

    private static string? Find(List<Group> groups, string parent, Piece piece)
    {
        foreach (var g in groups)
        {
            var path = parent == "" ? g.Id : $"{parent}/{g.Id}";
            if (g.Children is { Count: > 0 } children && Find(children, path, piece) is { } inner) return inner;
            if (g.Match is { Unsupported: false } m && Takes(m, piece)) return path;
        }
        return null;
    }

    private static bool Takes(Match m, Piece p)
        => (m.Kinds is null || m.Kinds.Contains(p.Kind))
            && (m.Waypoints is null || p.Waypoint is not null && m.Waypoints.Contains(p.Waypoint))
            && (m.Name is null || Regex.IsMatch(p.Name, m.Name))
            && (!m.Trees || p.Tree)
            && (!m.Ground || p.Ground)
            && (!m.OnConverted || p.OnConverted);
}
