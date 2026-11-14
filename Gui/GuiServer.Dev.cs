using System.Text.Json;
using System.Text.RegularExpressions;

namespace TmSurfaceEditor;

// tools/gui-dev only: edits the repo's data files
internal sealed partial class GuiServer
{
    private sealed record NamesAsk(List<string> Names);

    // POST { "names": [...] } → added to notSupported in swaps.jsonc, unless a pattern there already takes them
    private Task AddNotSupported(Call c)
    {
        var names = NamesIn(c);
        var rules = ConversionRules.Load(dataDir);
        EditNotSupported(entries => entries.AddRange(names.Where(n => !rules.Unsupported(n)).Order()));
        return Text(c.Response, 200, "ok");
    }

    // POST { "names": [...] } → their own entries go; a name a pattern takes is refused (it would free the pattern's others)
    private Task RemoveNotSupported(Call c)
    {
        var names = NamesIn(c);
        EditNotSupported(entries =>
        {
            var patterns = names.SelectMany(n => entries.Where(e => e != n && ConversionRules.Matches(n, e)).Select(e => $"{n} ({e})")).ToList();
            if (patterns.Count > 0) throw new Refused(409, $"In a pattern, edit swaps.jsonc: {string.Join(", ", patterns)}");
            entries.RemoveAll(names.Contains);
        });
        return Text(c.Response, 200, "ok");
    }

    private List<string> NamesIn(Call c)
    {
        if (devPage is null) throw new Refused(403, "dev mode only");
        return [.. (Read<NamesAsk>(c.Body)?.Names ?? throw new Refused(400, "no names")).Distinct()];
    }

    private void EditNotSupported(Action<List<string>> change)
    {
        var path = Path.Combine(dataDir, "swaps.jsonc");
        var text = File.ReadAllText(path);
        var list = Regex.Match(text, @"""notSupported""\s*:\s*(\[[^\]]*\])");
        if (!list.Success) throw new Refused(500, "no notSupported list in swaps.jsonc");
        var at = list.Groups[1];
        var entries = JsonSerializer.Deserialize<List<string>>(at.Value)!;
        change(entries);
        var written = "[" + string.Join(", ", entries.Select(e => JsonSerializer.Serialize(e))) + "]";
        File.WriteAllText(path, text[..at.Index] + written + text[(at.Index + at.Length)..]);
    }
}
