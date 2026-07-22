using GitMcp.Core;

namespace GitMcp.Tests;

public sealed class GitSceneTests
{
    [Fact]
    public void CountPorcelain_counts_staged_unstaged_untracked()
    {
        var c = GitScene.CountPorcelain("""
            M  staged.txt
             M unstaged.txt
            MM both.txt
            ?? new.txt
            A  added.txt
            """);
        Assert.Equal(3, c.Staged); // M , MM, A
        Assert.Equal(2, c.Unstaged); //  M, MM
        Assert.Equal(1, c.Untracked);
        Assert.True(c.IsDirty);
    }

    [Fact]
    public void ParseLeftRightCount_tab_or_space()
    {
        Assert.Equal((2, 3), GitScene.ParseLeftRightCount("2\t3"));
        Assert.Equal((0, 1), GitScene.ParseLeftRightCount("0 1\n"));
        Assert.Null(GitScene.ParseLeftRightCount(""));
    }

    [Fact]
    public void ParseSubmoduleStatus_flags_and_hint()
    {
        var entries = GitScene.ParseSubmoduleStatus("""
             abcdef1 cascade-ide (heads/develop)
            +fedcba2 Financial/software/open
            -0000000 missing-mod
            """, 10);
        Assert.Equal(3, entries.Count);
        Assert.Equal(" ", entries[0].Flag);
        Assert.Equal("cascade-ide", entries[0].Path);
        Assert.Equal("heads/develop", entries[0].BranchHint);
        Assert.Equal("+", entries[1].Flag);
        Assert.Equal("Financial/software/open", entries[1].Path);
        Assert.Equal("-", entries[2].Flag);
    }
}
