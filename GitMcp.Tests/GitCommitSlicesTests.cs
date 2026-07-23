using System.Text.Json;
using GitMcp;

namespace GitMcp.Tests;

public sealed class GitCommitSlicesTests
{
    [Fact]
    public void Commit_slices_without_paths_returns_partial_errors()
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["message"] = JsonSerializer.SerializeToElement("test"),
            ["slices"] = JsonSerializer.SerializeToElement(new[]
            {
                new { root = Path.GetTempPath().TrimEnd('\\', '/'), paths = Array.Empty<string>() }
            })
        };

        var raw = ToolHandlers.Handle("git_commit", args);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("git_commit_slices/v0", doc.RootElement.GetProperty("schema").GetString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var slice = doc.RootElement.GetProperty("slices")[0];
        Assert.False(slice.GetProperty("ok").GetBoolean());
        Assert.Contains("paths", slice.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
