using Nomad.Core.Code;

namespace Nomad.Core.Tests;

public class LineDiffTests
{
    [Fact]
    public void ChangedAddedAndRemovedLinesBecomeHunks()
    {
        string[] before = ["a", "b", "c", "d", "e"];
        string[] after = ["a", "B", "c", "x", "y", "e", "f"];

        Assert.Equal(
        [
            new DiffHunk(1, 1, 1, 1), // b -> B
            new DiffHunk(3, 1, 3, 2), // d -> x, y
            new DiffHunk(5, 0, 6, 1), // + f
        ], LineDiff.Compute(before, after));
    }

    [Fact]
    public void SameTextHasNoHunksAndADeletionHasNoNewLines()
    {
        Assert.Empty(LineDiff.Compute(["a", "b"], ["a", "b"]));
        Assert.Equal([new DiffHunk(1, 2, 1, 0)], LineDiff.Compute(["a", "b", "c", "d"], ["a", "d"]));
    }
}
