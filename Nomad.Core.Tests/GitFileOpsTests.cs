using Nomad.Core.Git;

namespace Nomad.Core.Tests;

public class GitFileOpsTests
{
    private static async Task<string> Git(string folder, params string[] args)
    {
        var (ok, output, error) = await GitInfo.RunToolAsync("git", ["-C", folder, "-c", "user.name=t", "-c", "user.email=t@t", .. args], null, TimeSpan.FromSeconds(30));
        Assert.True(ok, error);
        return output;
    }

    [Fact]
    public async Task StagesUndoesAndCommitsOnlyThePathGiven()
    {
        var root = Path.Combine(Path.GetTempPath(), "nomad-gitops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "a.txt"), "one\n");
            await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "two\n");
            await Git(root, "init", "-q");
            await Git(root, "add", "-A");
            await Git(root, "commit", "-qm", "init");

            await File.WriteAllTextAsync(Path.Combine(root, "src", "a.txt"), "changed\n");
            await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "changed\n");

            // Stage a file, then take it out again.
            Assert.Null(await GitFileOps.StageAsync(Path.Combine(root, "b.txt")));
            Assert.Equal("M  b.txt", (await Git(root, "status", "--porcelain")).Split('\n').First(l => l.EndsWith("b.txt")).TrimEnd());
            Assert.Null(await GitFileOps.UnstageAsync(Path.Combine(root, "b.txt")));
            Assert.Equal(" M b.txt", (await Git(root, "status", "--porcelain")).Split('\n').First(l => l.EndsWith("b.txt")).TrimEnd());

            // Commit a folder: b.txt stays changed and uncommitted.
            Assert.Null(await GitFileOps.CommitAsync(Path.Combine(root, "src"), "src only"));
            Assert.Equal(" M b.txt", (await Git(root, "status", "--porcelain")).TrimEnd());
            Assert.Equal("src only", (await Git(root, "log", "-1", "--format=%s")).Trim());

            // Undo a file's changes.
            Assert.Null(await GitFileOps.RevertAsync(Path.Combine(root, "b.txt")));
            Assert.Equal("", (await Git(root, "status", "--porcelain")).Trim());
            Assert.Equal("two\n", (await File.ReadAllTextAsync(Path.Combine(root, "b.txt"))).Replace("\r\n", "\n"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }
}
