using GitMcp.Core;

namespace GitMcp.Tests;

public sealed class GitCommandBuilderTests
{
    [Fact]
    public void StatusShortBranch_matches_three_tokens()
    {
        var a = GitCommandBuilder.StatusShortBranch();
        Assert.Equal(["status", "--short", "--branch"], a);
    }

    [Fact]
    public void Push_mcp_defaults_origin_when_empty_remote()
    {
        var mcp = GitCommandBuilder.Push(null, null, defaultOriginWhenRemoteEmpty: true);
        Assert.Equal(["push", "origin"], mcp);
        var ide = GitCommandBuilder.Push(null, null, defaultOriginWhenRemoteEmpty: false);
        Assert.Equal(["push"], ide);
    }

    [Fact]
    public void Fetch_rejects_remote_with_all()
    {
        var r = GitCommandBuilder.Fetch(all: true, prune: true, remote: "origin");
        Assert.False(r.IsSuccess);
        Assert.Contains("all=true", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Pull_rejects_mismatched_remote_branch()
    {
        var r = GitCommandBuilder.Pull("origin", null, ffOnly: true);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void Log_clamps_count()
    {
        var a = GitCommandBuilder.Log(9999);
        Assert.Contains(GitCommandBuilder.LogCountMax.ToString(), string.Join(" ", a), StringComparison.Ordinal);
    }
}
