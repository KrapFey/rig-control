using System.Text.Json;

namespace RigControl.Core;

/// <summary>One line of the supported-devices list (tools/gen-supported-devices.py builds it from each source).</summary>
public sealed record SupportedDevice(string Name, string Kind, string Features, string Source);

public static class SupportedDevices
{
    static readonly Lazy<List<SupportedDevice>> All = new(() =>
    {
        using var s = typeof(SupportedDevices).Assembly.GetManifestResourceStream("RigControl.Core.supported-devices.json")!;
        using var doc = JsonDocument.Parse(s);
        return [.. doc.RootElement.GetProperty("devices").EnumerateArray().Select(d => new SupportedDevice(
            d.GetProperty("name").GetString()!, d.GetProperty("kind").GetString()!,
            d.GetProperty("features").GetString()!, d.GetProperty("source").GetString()!))];
    });

    public static IReadOnlyList<SupportedDevice> List => All.Value;

    /// <summary>
    /// Fuzzy search: every word of the query has to match the name (or the kind/source), as a substring or as
    /// letters in order ("dav2" finds "DeathAdder V2"). Best matches first.
    /// </summary>
    public static List<SupportedDevice> Search(string query, string? kind = null)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [.. List.Where(d => kind is null || d.Kind.Split(" · ").Contains(kind))];
        return [.. List
            .Where(d => kind is null || d.Kind.Split(" · ").Contains(kind))
            .Select(d => (d, score: Score(d, words)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.d.Name.Length).ThenBy(x => x.d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.d)];
    }

    static int Score(SupportedDevice d, string[] words)
    {
        if (words.Length == 0) return 1;
        string name = d.Name.ToLowerInvariant(), rest = $"{d.Kind} {d.Source}".ToLowerInvariant();
        int total = 0;
        foreach (var w in words)
        {
            int s = Match(name, w);
            if (s == 0) s = Match(rest, w) / 4; // matching only the kind or source counts for less
            if (s == 0) return 0;
            total += s;
        }
        return total;
    }

    /// <summary>How well <paramref name="word"/> matches <paramref name="text"/>: 0 = not at all.</summary>
    internal static int Match(string text, string word)
    {
        // Every place the word occurs counts; the best one wins (the "x" in "nzxt x2" is the one that starts "x2").
        int best = 0;
        for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
            best = Math.Max(best, 1000 + (at == 0 || !char.IsLetterOrDigit(text[at - 1]) ? 500 : 0) - Math.Min(at, 100) / 10);
        if (best > 0) return best;
        // Letters in order; each one right after the previous, or at a word start, scores more.
        int score = 0, i = 0, last = -2;
        foreach (char c in word)
        {
            while (i < text.Length && text[i] != c) i++;
            if (i == text.Length) return 0;
            score += i == last + 1 ? 30 : i == 0 || !char.IsLetterOrDigit(text[i - 1]) ? 20 : 5;
            last = i++;
        }
        return score;
    }
}
