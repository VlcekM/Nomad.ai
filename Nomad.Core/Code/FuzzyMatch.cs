namespace Nomad.Core.Code;

/// <summary>
/// Matches a query against a name as Visual Studio's Go To does, ignoring case: the whole name, its start, its word
/// starts ("CH" or "cliHel" for ClientHello, "cs" for client_server), or anywhere in it. A better match scores higher;
/// the matched characters' positions are returned for highlighting.
/// </summary>
public static class FuzzyMatch
{
    public static bool TryMatch(string name, string query, out int score, out int[] positions)
    {
        score = 0;
        positions = [];
        if (query.Length == 0 || query.Length > name.Length)
        {
            return false;
        }

        var at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at == 0)
        {
            score = name.Length == query.Length ? 1000 : 900 - Math.Min(100, name.Length - query.Length);
            if (name.StartsWith(query, StringComparison.Ordinal))
            {
                score += 5;
            }

            positions = Range(0, query.Length);
            return true;
        }

        if (Humps(name, query) is { } humps)
        {
            score = 800 - Math.Min(100, humps.Segments * 10 + name.Length / 4);
            positions = humps.Positions;
            return true;
        }

        if (at > 0)
        {
            score = (IsWordStart(name, at) ? 700 : 600) - Math.Min(100, at + name.Length / 4);
            positions = Range(at, query.Length);
            return true;
        }

        return false;
    }

    private static int[] Range(int start, int count)
    {
        var positions = new int[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = start + i;
        }

        return positions;
    }

    /// <summary>A word start: the first character, an upper-case letter after a lower-case one, or after a separator or digit change.</summary>
    public static bool IsWordStart(string name, int i)
    {
        if (i == 0)
        {
            return true;
        }

        char prev = name[i - 1], c = name[i];
        return char.IsUpper(c) && !char.IsUpper(prev) ||
               char.IsLetterOrDigit(c) && !char.IsLetterOrDigit(prev) ||
               char.IsDigit(c) && !char.IsDigit(prev) ||
               // The last capital of a run that a word follows: the "H" of "HTMLHelper".
               char.IsUpper(c) && i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(prev);
    }

    // Each query character goes on where the last left off, or at a later word start; the first must be a word start.
    private static (int[] Positions, int Segments)? Humps(string name, string query)
    {
        var positions = new int[query.Length];
        var budget = 2000; // Backtracking steps: a long name with many candidates gives up rather than stalls.
        return Match(0, -1) ? (positions, Segments(positions)) : null;

        bool Match(int q, int last)
        {
            if (q == query.Length)
            {
                return true;
            }

            if (--budget < 0)
            {
                return false;
            }

            var want = char.ToUpperInvariant(query[q]);

            // Going on in the same word first, then each later word start.
            if (last >= 0 && last + 1 < name.Length && char.ToUpperInvariant(name[last + 1]) == want)
            {
                positions[q] = last + 1;
                if (Match(q + 1, last + 1))
                {
                    return true;
                }
            }

            for (var i = last + 1; i < name.Length; i++)
            {
                if (i != last + 1 || last < 0)
                {
                    if (char.ToUpperInvariant(name[i]) == want && IsWordStart(name, i))
                    {
                        positions[q] = i;
                        if (Match(q + 1, i))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }

    private static int Segments(int[] positions)
    {
        var segments = 1;
        for (var i = 1; i < positions.Length; i++)
        {
            if (positions[i] != positions[i - 1] + 1)
            {
                segments++;
            }
        }

        return segments;
    }
}
