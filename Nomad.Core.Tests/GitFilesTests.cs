using Nomad.Core.Git;

namespace Nomad.Core.Tests;

public class GitFilesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "repo");

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void FilesGetTheirStateAndFoldersCountTheChangesBelowThem()
    {
        var files = GitFiles.Parse(Root.Replace('\\', '/'),
            " M src/a.cs\0?? src/new/b.cs\0 D src/gone.cs\0R  src/c.cs\0src/old.cs\0!! bin/\0A  d.cs\0");

        Assert.Equal(GitFileState.Modified, files.StateOf(At("src", "a.cs")));
        Assert.Equal(GitFileState.Added, files.StateOf(At("src", "new", "b.cs")));
        Assert.Equal(GitFileState.Modified, files.StateOf(At("src", "c.cs")));
        Assert.Equal(GitFileState.Added, files.StateOf(At("d.cs")));
        Assert.Equal(GitFileState.Clean, files.StateOf(At("src", "same.cs")));
        Assert.Equal(GitFileState.None, files.StateOf(At("bin", "x.dll")));
        Assert.Equal(GitFileState.None, files.StateOf(Path.Combine(Path.GetTempPath(), "elsewhere.cs")));

        Assert.Equal((2, 1, 1), files.CountsUnder(At("src")));
        Assert.Equal((0, 1, 0), files.CountsUnder(At("src", "new")));
        Assert.Equal((2, 2, 1), files.CountsUnder(Root));
        Assert.Equal((0, 0, 0), files.CountsUnder(At("bin")));
    }
}
