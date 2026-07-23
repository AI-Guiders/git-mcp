using GitMcp.Core;

namespace GitMcp.Tests;

public sealed class GitDiffSceneTests
{
    [Fact]
    public void ParseNumstat_basic_and_binary_dash()
    {
        var rows = GitDiffScene.ParseNumstat("""
            10	2	src/A.cs
            -	-	bin/x.png
            1	0	old.txt => new.txt
            """, "unstaged", 10);
        Assert.Equal(3, rows.Count);
        Assert.Equal(("src/A.cs", 10, 2), (rows[0].Path, rows[0].Additions, rows[0].Deletions));
        Assert.Equal(0, rows[1].Additions);
        Assert.Equal("new.txt", rows[2].Path);
    }

    [Fact]
    public void ParseUnifiedDiff_hunks_and_ops()
    {
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1,3 +1,4 @@
             keep
            -old
            +new
            +more
            """;
        var (hunks, truncated) = GitDiffScene.ParseUnifiedDiff(diff, 10, 50);
        Assert.False(truncated);
        Assert.Single(hunks);
        Assert.StartsWith("@@", hunks[0].Header);
        Assert.Contains(hunks[0].Lines, l => l.Op == "-" && l.Text == "old");
        Assert.Contains(hunks[0].Lines, l => l.Op == "+" && l.Text == "new");
    }
}
