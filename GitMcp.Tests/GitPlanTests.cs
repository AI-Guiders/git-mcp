using System.Text.Json;
using GitMcp;
using GitMcp.Core;

namespace GitMcp.Tests;

public sealed class GitPlanTests
{
    [Fact]
    public void ParsePorcelainPaths_handles_rename_and_untracked()
    {
        var porcelain = """
            M  a.cs
             M b.cs
            ?? c.md
            R  old.txt -> new.txt
            """;
        var paths = GitPlan.ParsePorcelainPaths(porcelain, 50);
        Assert.Equal(["a.cs", "b.cs", "c.md", "new.txt"], paths);
    }

    [Fact]
    public void Plan_validate_requires_message_and_paths()
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["op"] = JsonSerializer.SerializeToElement("validate"),
            ["check_dirty"] = JsonSerializer.SerializeToElement(false),
            ["slices"] = JsonSerializer.SerializeToElement(new[]
            {
                new { root = Path.GetTempPath().TrimEnd('\\', '/'), paths = Array.Empty<string>(), message = (string?)null }
            })
        };

        var raw = ToolHandlers.Handle("git_plan", args);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(GitPlan.SchemaVersion, doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("validate", doc.RootElement.GetProperty("op").GetString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var errs = doc.RootElement.GetProperty("slices")[0].GetProperty("errors");
        Assert.True(errs.GetArrayLength() >= 2);
    }

    [Fact]
    public void Plan_draft_requires_roots()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ToolHandlers.Handle("git_plan", new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["op"] = JsonSerializer.SerializeToElement("draft")
            }));
        Assert.Contains("roots", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_draft_then_validate_against_live_git_mcp_tree()
    {
        var gm = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(gm, "GitMcp.csproj")), "test host should resolve git-mcp root");

        var draftRaw = ToolHandlers.Handle("git_plan", new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["op"] = JsonSerializer.SerializeToElement("draft"),
            ["workspace_path"] = JsonSerializer.SerializeToElement(gm)
        });
        using var draft = JsonDocument.Parse(draftRaw);
        Assert.Equal(GitPlan.SchemaVersion, draft.RootElement.GetProperty("schema").GetString());
        Assert.True(draft.RootElement.GetProperty("ok").GetBoolean());

        var rootEl = draft.RootElement.GetProperty("roots")[0];
        Assert.True(rootEl.GetProperty("ok").GetBoolean());
        if (!rootEl.GetProperty("dirty").GetBoolean())
            return; // clean tree — draft shape already asserted

        var path = rootEl.GetProperty("paths")[0].GetString();
        Assert.False(string.IsNullOrWhiteSpace(path));

        var validateRaw = ToolHandlers.Handle("git_plan", new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["op"] = JsonSerializer.SerializeToElement("validate"),
            ["slices"] = JsonSerializer.SerializeToElement(new[]
            {
                new { root = gm, paths = new[] { path }, message = "test: git_plan validate dogfood" }
            })
        });
        using var validate = JsonDocument.Parse(validateRaw);
        Assert.True(validate.RootElement.GetProperty("ok").GetBoolean(), validateRaw);
    }
}
