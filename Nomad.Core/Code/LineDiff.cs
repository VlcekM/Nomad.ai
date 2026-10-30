namespace Nomad.Core.Code;

/// <summary>A run of lines that differ: <see cref="OldCount"/> lines from <see cref="OldStart"/> became <see cref="NewCount"/> from <see cref="NewStart"/> (0-based).</summary>
public readonly record struct DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount);

/// <summary>
/// The lines that differ between two versions of a text: the common start and end are set aside, and the rest is
/// matched by longest common subsequence (when it is small enough; otherwise it counts as changed whole).
/// </summary>
public static class LineDiff
{
    private const long MaxCells = 4_000_000;

    public static List<DiffHunk> Compute(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var hunks = new List<DiffHunk>();
        int start = 0, endA = a.Count, endB = b.Count;
        while (start < endA && start < endB && a[start] == b[start])
        {
            start++;
        }

        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }

        int n = endA - start, m = endB - start;
        if (n == 0 && m == 0)
        {
            return hunks;
        }

        if (n == 0 || m == 0 || (long)n * m > MaxCells)
        {
            hunks.Add(new DiffHunk(start, n, start, m));
            return hunks;
        }

        // lcs[i, j]: the common subsequence of a[start+i..] and b[start+j..], walked from the front.
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0, y = 0, hunkA = -1, hunkB = -1;
        void Close()
        {
            if (hunkA >= 0)
            {
                hunks.Add(new DiffHunk(start + hunkA, x - hunkA, start + hunkB, y - hunkB));
                hunkA = hunkB = -1;
            }
        }

        while (x < n || y < m)
        {
            if (x < n && y < m && a[start + x] == b[start + y])
            {
                Close();
                x++;
                y++;
                continue;
            }

            if (hunkA < 0)
            {
                (hunkA, hunkB) = (x, y);
            }

            if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                y++;
            }
            else
            {
                x++;
            }
        }

        Close();
        return hunks;
    }
}
