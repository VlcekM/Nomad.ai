namespace Nomad.Core.Lsp;

/// <summary>
/// Which completions fit what has been typed of a name, best first, as Visual Studio's list: those it starts (in the
/// same case first), then those whose capitals it spells ("SB" for StringBuilder), then those it is in; ties go by the
/// server's own order. With nothing typed, all of them in the server's order.
/// </summary>
public static class CompletionFilter
{
    /// <summary>A completion that fits, and the characters of its label the typing matched (to show them bold).</summary>
    public readonly record struct Ranked(LspCompletion Item, int Score, int[] Matched);

    public static List<Ranked> Rank(IEnumerable<LspCompletion> items, string typed)
    {
        var ranked = new List<Ranked>();
        foreach (var item in items)
        {
            if (Score(item.FilterText, typed) is { } match)
            {
                // The matched characters are the filter text's; shown on the label when that starts the same way.
                var shown = item.Label.StartsWith(item.FilterText, StringComparison.Ordinal) ? match.Matched : [];
                ranked.Add(new Ranked(item, match.Score, shown));
            }
        }

        ranked.Sort((a, b) =>
        {
            var byScore = a.Score.CompareTo(b.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var bySort = string.CompareOrdinal(a.Item.SortText, b.Item.SortText);
            return bySort != 0 ? bySort : string.CompareOrdinal(a.Item.Label, b.Item.Label);
        });
        return ranked;
    }

    /// <summary>The one to select: the best match; with nothing typed, the one the server picked (if any).</summary>
    public static int Best(List<Ranked> ranked, string typed)
    {
        if (ranked.Count == 0)
        {
            return -1;
        }

        if (typed.Length == 0)
        {
            var preselected = ranked.FindIndex(r => r.Item.Preselect);
            return Math.Max(0, preselected);
        }

        // The exact name typed, if it is there (in either case), else the one the server picked (the enum an argument
        // takes) if it fits as well as the first, else the first.
        var exact = ranked.FindIndex(r => string.Equals(r.Item.FilterText, typed, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0)
        {
            return exact;
        }

        var picked = ranked.FindIndex(r => r.Item.Preselect && r.Score == ranked[0].Score);
        return Math.Max(0, picked);
    }

    // 0: starts with it in the same case, 1: in another case, 2: its capitals, 3: contains it; null: doesn't fit.
    private static (int Score, int[] Matched)? Score(string text, string typed)
    {
        if (typed.Length == 0)
        {
            return (0, []);
        }

        if (text.StartsWith(typed, StringComparison.Ordinal))
        {
            return (0, Enumerable.Range(0, typed.Length).ToArray());
        }

        if (text.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            return (1, Enumerable.Range(0, typed.Length).ToArray());
        }

        if (Humps(text, typed) is { } humps)
        {
            return (2, humps);
        }

        var at = text.IndexOf(typed, StringComparison.OrdinalIgnoreCase);
        return at > 0 ? (3, Enumerable.Range(at, typed.Length).ToArray()) : null;
    }

    // Each typed character starts a word of the name (a capital, or after '_'), or carries on the one before it.
    private static int[]? Humps(string text, string typed)
    {
        var matched = new List<int>(typed.Length);
        var i = 0;
        foreach (var c in typed)
        {
            var continues = matched.Count > 0 && i < text.Length && char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(c) && !IsHump(text, i);
            if (!continues)
            {
                while (i < text.Length && !(IsHump(text, i) && char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(c)))
                {
                    i++;
                }
            }

            if (i >= text.Length)
            {
                return null;
            }

            matched.Add(i++);
        }

        return [.. matched];
    }

    private static bool IsHump(string text, int i) =>
        i == 0 || char.IsUpper(text[i]) && !char.IsUpper(text[i - 1]) || text[i - 1] == '_' && text[i] != '_' ||
        char.IsUpper(text[i]) && i + 1 < text.Length && char.IsLower(text[i + 1]);
}
