using Nomad.Core.Files;

namespace Nomad.Core.Tests;

public class DirectoryListingTests
{
    [Fact]
    public void FoldersComeFirstThenFilesByNaturalNameAndGitIsLeftOut()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, $"listing-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, ".git"));
            Directory.CreateDirectory(Path.Combine(folder, "src"));
            Directory.CreateDirectory(Path.Combine(folder, "Assets"));
            foreach (var name in (string[])["file10.cs", "file2.cs", "README.md", "app.cs"])
            {
                File.WriteAllText(Path.Combine(folder, name), "");
            }

            var names = DirectoryListing.List(folder).Select(e => e.Name).ToArray();

            Assert.Equal(["Assets", "src", "app.cs", "file2.cs", "file10.cs", "README.md"], names);
            Assert.True(DirectoryListing.List(folder)[0].IsDirectory);
            Assert.Empty(DirectoryListing.List(Path.Combine(folder, "missing")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
