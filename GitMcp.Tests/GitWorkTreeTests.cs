using GitMcp.Core;

namespace GitMcp.Tests;

public sealed class GitWorkTreeTests
{
    [Fact]
    public void GetRepoRoot_accepts_dot_git_directory()
    {
        var d = NewTempDir();
        Directory.CreateDirectory(Path.Combine(d, ".git"));
        try
        {
            Assert.Equal(Path.GetFullPath(d), GitWorkTree.GetRepoRoot(d));
        }
        finally
        {
            Directory.Delete(d, recursive: true);
        }
    }

    [Fact]
    public void GetRepoRoot_accepts_dot_git_file_like_submodule()
    {
        var d = NewTempDir();
        File.WriteAllText(Path.Combine(d, ".git"), "gitdir: ../../../.git/modules/foo\n");
        try
        {
            Assert.Equal(Path.GetFullPath(d), GitWorkTree.GetRepoRoot(d));
        }
        finally
        {
            Directory.Delete(d, recursive: true);
        }
    }

    [Fact]
    public void GetRepoRoot_rejects_plain_directory_without_git()
    {
        var d = NewTempDir();
        try
        {
            var ex = Assert.Throws<ArgumentException>(() => GitWorkTree.GetRepoRoot(d));
            Assert.Contains("Not a git repository", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(d, recursive: true);
        }
    }

    [Fact]
    public void IsGitWorkTreeRoot_false_for_random_dir()
    {
        var d = NewTempDir();
        try
        {
            Assert.False(GitWorkTree.IsGitWorkTreeRoot(d));
        }
        finally
        {
            Directory.Delete(d, recursive: true);
        }
    }

    static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "gitmcp-worktree-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }
}
