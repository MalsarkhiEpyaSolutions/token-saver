using System.Text.Json.Nodes;
using TokenStack.Core.Claude;
using Xunit;

namespace TokenStack.Tests;

public class ClaudeSurgeonTests
{
    private const string RealisticSettings = """
    {
      "env": { "ANTHROPIC_BASE_URL": "http://127.0.0.1:8787" },
      "permissions": { "defaultMode": "auto" },
      "hooks": {
        "SessionStart": [
          { "matcher": "startup|resume", "hooks": [
              { "type": "command", "command": "\"D:\\proxy tokens\\venv\\Scripts\\headroom.exe\" init hook ensure --profile init-user --marker headroom-init-claude", "timeout": 15 } ] },
          { "matcher": "startup|resume", "hooks": [
              { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\u\\.claude\\hooks\\ensure-stack.ps1\"", "timeout": 30 } ] }
        ],
        "PreToolUse": [
          { "matcher": "Bash", "hooks": [
              { "type": "command", "command": "\"D:\\proxy tokens\\rtk\\rtk.exe\" hook claude" } ] }
        ]
      },
      "enabledPlugins": { "superpowers@claude-plugins-official": true },
      "extraKnownMarketplaces": { "x": { "source": { "source": "github", "repo": "a/b" } } },
      "theme": "dark-daltonized"
    }
    """;

    private static JsonNode Parse(string s) => JsonNode.Parse(s)!;

    // ---------- RTK PreToolUse hook ----------

    /// <summary>`rtk verify` only reports the bare `rtk hook claude` spelling, so that form gets
    /// added by hand beside ours. Both filter identically (measured), but two entries mean rtk
    /// is spawned twice on every Bash call — and while we matched on "rtk.exe" alone, the
    /// hand-added copy was invisible to us and survived every re-install.</summary>
    [Fact]
    public void EnsureRtkHook_CollapsesAHandAddedBareHook()
    {
        var root = Parse("""
        {"hooks":{"PreToolUse":[
          {"matcher":"Bash","hooks":[{"type":"command","command":"rtk hook claude"}]},
          {"matcher":"Bash","hooks":[{"type":"command","command":"\"C:\\ts\\rtk\\rtk.exe\" hook claude"}]}
        ]}}
        """);
        Assert.Equal(2, ClaudeSurgeon.CountRtkHooks(root));

        Assert.True(ClaudeSurgeon.EnsureRtkHook(root, @"C:\ts\rtk\rtk.exe", "Bash"));

        Assert.Equal(1, ClaudeSurgeon.CountRtkHooks(root));
        Assert.Equal("rtk hook claude",
            root["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    /// <summary>The bare spelling is the default because it is the only one `rtk verify`
    /// recognizes; the full path stays reachable as doctor's fallback for a broken PATH.</summary>
    [Fact]
    public void EnsureRtkHook_WritesBareByDefault_AndFullPathOnDemand()
    {
        var bare = Parse("{}");
        ClaudeSurgeon.EnsureRtkHook(bare, @"C:\ts\rtk\rtk.exe", "Bash");
        Assert.Equal("rtk hook claude",
            bare["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());

        var full = Parse("{}");
        ClaudeSurgeon.EnsureRtkHook(full, @"C:\ts\rtk\rtk.exe", "Bash", bare: false);
        Assert.Equal(@"""C:\ts\rtk\rtk.exe"" hook claude",
            full["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());

        // and each form replaces the other in place, never stacking a second entry
        Assert.True(ClaudeSurgeon.EnsureRtkHook(full, @"C:\ts\rtk\rtk.exe", "Bash"));
        Assert.Equal(1, ClaudeSurgeon.CountRtkHooks(full));
    }

    [Fact]
    public void RemoveRtkHook_TakesEverySpelling_AndSparesForeignHooks()
    {
        var root = Parse("""
        {"hooks":{"PreToolUse":[
          {"matcher":"Bash","hooks":[{"type":"command","command":"rtk hook claude"}]},
          {"matcher":"Bash","hooks":[{"type":"command","command":"\"C:\\ts\\rtk\\rtk.exe\" hook claude"}]},
          {"matcher":"Bash","hooks":[{"type":"command","command":"my-own-linter --check"}]}
        ]}}
        """);
        Assert.True(ClaudeSurgeon.RemoveRtkHook(root));
        Assert.Equal(0, ClaudeSurgeon.CountRtkHooks(root));

        var left = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Single(left);
        Assert.Equal("my-own-linter --check", left[0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void CountRtkHooks_IsZeroWhenNothingIsWired()
        => Assert.Equal(0, ClaudeSurgeon.CountRtkHooks(Parse("""{"hooks":{"PreToolUse":[]}}""")));

    [Fact]
    public void EnsureRtkHook_RewritesStalePath_NoDuplicate()
    {
        var root = Parse(RealisticSettings);
        var changed = ClaudeSurgeon.EnsureRtkHook(root, @"C:\ts\rtk\rtk.exe", "Bash");
        Assert.True(changed);
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Single(pre);
        var cmd = pre[0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.Equal("rtk hook claude", cmd);
    }

    [Fact]
    public void EnsureRtkHook_Idempotent()
    {
        var root = Parse(RealisticSettings);
        ClaudeSurgeon.EnsureRtkHook(root, @"C:\ts\rtk\rtk.exe", "Bash");
        var changed = ClaudeSurgeon.EnsureRtkHook(root, @"C:\ts\rtk\rtk.exe", "Bash");
        Assert.False(changed);
        Assert.Single(root["hooks"]!["PreToolUse"]!.AsArray());
    }

    [Fact]
    public void EnsureRtkHook_CreatesStructure_WhenNoHooksObject()
    {
        var root = Parse("""{ "theme": "dark" }""");
        ClaudeSurgeon.EnsureRtkHook(root, @"C:\ts\rtk\rtk.exe", "Bash");
        Assert.Equal("Bash", root["hooks"]!["PreToolUse"]![0]!["matcher"]!.GetValue<string>());
        Assert.Equal("dark", root["theme"]!.GetValue<string>()); // untouched sibling
    }

    [Fact]
    public void RemoveRtkHook_RemovesOnlyOurs()
    {
        var root = Parse(RealisticSettings);
        var changed = ClaudeSurgeon.RemoveRtkHook(root);
        Assert.True(changed);
        Assert.Empty(root["hooks"]!["PreToolUse"]!.AsArray());
        Assert.NotNull(root["hooks"]!["SessionStart"]); // untouched
    }

    // ---------- SessionStart status hook ----------

    [Fact]
    public void EnsureSessionStatusHook_ReplacesBothLegacyEntries_WithSingleExeCall()
    {
        var root = Parse(RealisticSettings);
        var changed = ClaudeSurgeon.EnsureSessionStatusHook(root, @"C:\ts\token-saver.exe");
        Assert.True(changed);
        var ss = root["hooks"]!["SessionStart"]!.AsArray();
        Assert.Single(ss);
        var entry = ss[0]!;
        Assert.Equal("startup|resume", entry["matcher"]!.GetValue<string>());
        var hook = entry["hooks"]![0]!;
        Assert.Equal("\"C:\\ts\\token-saver.exe\" status --hook", hook["command"]!.GetValue<string>());
        Assert.Equal(30, hook["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void EnsureSessionStatusHook_MigratesLegacyExeName()
    {
        var root = Parse("""
        { "hooks": { "SessionStart": [
          { "matcher": "startup|resume", "hooks": [
            { "type": "command", "command": "\"C:\\token-stack\\token-stack.exe\" status --hook" } ] } ] } }
        """);
        var changed = ClaudeSurgeon.EnsureSessionStatusHook(root, @"C:\token-stack\token-saver.exe");
        Assert.True(changed);
        var ss = root["hooks"]!["SessionStart"]!.AsArray();
        Assert.Single(ss); // old replaced, not duplicated
        Assert.Equal("\"C:\\token-stack\\token-saver.exe\" status --hook",
            ss[0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void EnsureSessionStatusHook_PreservesForeignSessionStartEntries()
    {
        var root = Parse("""
        { "hooks": { "SessionStart": [
            { "matcher": "startup", "hooks": [ { "type": "command", "command": "my-own-tool.exe" } ] }
        ] } }
        """);
        ClaudeSurgeon.EnsureSessionStatusHook(root, @"C:\ts\token-saver.exe");
        var ss = root["hooks"]!["SessionStart"]!.AsArray();
        Assert.Equal(2, ss.Count); // foreign + ours
        Assert.Equal("my-own-tool.exe", ss[0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveSessionStatusHook_Idempotent()
    {
        var root = Parse(RealisticSettings);
        ClaudeSurgeon.EnsureSessionStatusHook(root, @"C:\ts\token-saver.exe");
        Assert.True(ClaudeSurgeon.RemoveSessionStatusHook(root));
        Assert.False(ClaudeSurgeon.RemoveSessionStatusHook(root));
        Assert.Empty(root["hooks"]!["SessionStart"]!.AsArray());
    }

    // ---------- env.ANTHROPIC_BASE_URL ----------

    [Fact]
    public void SetEnvBaseUrl_PreservesSiblings()
    {
        var root = Parse("""{ "env": { "OTHER": "x" }, "theme": "dark" }""");
        ClaudeSurgeon.SetEnvBaseUrl(root, "http://127.0.0.1:9000");
        Assert.Equal("http://127.0.0.1:9000", root["env"]!["ANTHROPIC_BASE_URL"]!.GetValue<string>());
        Assert.Equal("x", root["env"]!["OTHER"]!.GetValue<string>());
    }

    [Fact]
    public void SetEnvVar_AddsUpdatesAndNoOps()
    {
        var root = Parse("""{ "theme": "dark" }""");
        Assert.True(ClaudeSurgeon.SetEnvVar(root, "ANTHROPIC_AUTH_TOKEN", "sk-1")); // added
        Assert.Equal("sk-1", root["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
        Assert.True(ClaudeSurgeon.SetEnvVar(root, "ANTHROPIC_AUTH_TOKEN", "sk-2"));  // updated
        Assert.False(ClaudeSurgeon.SetEnvVar(root, "ANTHROPIC_AUTH_TOKEN", "sk-2")); // no-op
        Assert.Equal("dark", root["theme"]!.GetValue<string>());                    // sibling preserved
    }

    [Fact]
    public void RemoveEnvBaseUrl_DropsEmptyEnvObject()
    {
        var root = Parse("""{ "env": { "ANTHROPIC_BASE_URL": "http://127.0.0.1:8787" } }""");
        Assert.True(ClaudeSurgeon.RemoveEnvBaseUrl(root));
        Assert.Null(root["env"]);
    }

    // ---------- mcpServers.semble (in ~/.claude.json) ----------

    [Fact]
    public void EnsureSembleMcp_AddsAbsoluteExePath_PreservingOtherServers()
    {
        var root = Parse("""{ "mcpServers": { "playwright": { "command": "npx" } }, "projects": {} }""");
        var changed = ClaudeSurgeon.EnsureSembleMcp(root, @"C:\u\.local\bin\semble.exe");
        Assert.True(changed);
        var semble = root["mcpServers"]!["semble"]!;
        Assert.Equal("stdio", semble["type"]!.GetValue<string>());
        Assert.Equal(@"C:\u\.local\bin\semble.exe", semble["command"]!.GetValue<string>());
        Assert.Empty(semble["args"]!.AsArray());
        Assert.NotNull(root["mcpServers"]!["playwright"]); // untouched
        Assert.NotNull(root["projects"]);                   // untouched
    }

    [Fact]
    public void EnsureSembleMcp_RejectsUvxStyleCommand_ByRewriting()
    {
        var root = Parse("""{ "mcpServers": { "semble": { "type": "stdio", "command": "uvx", "args": ["--from","semble[mcp]","semble"] } } }""");
        var changed = ClaudeSurgeon.EnsureSembleMcp(root, @"C:\u\.local\bin\semble.exe");
        Assert.True(changed);
        Assert.Equal(@"C:\u\.local\bin\semble.exe",
            root["mcpServers"]!["semble"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveSembleMcp_LeavesOtherServers()
    {
        var root = Parse("""{ "mcpServers": { "semble": { "command": "x" }, "playwright": { "command": "npx" } } }""");
        Assert.True(ClaudeSurgeon.RemoveSembleMcp(root));
        Assert.Null(root["mcpServers"]!["semble"]);
        Assert.NotNull(root["mcpServers"]!["playwright"]);
    }

    // ---------- file editor + backup ----------

    [Fact]
    public void Editor_SaveWithBackup_WritesTimestampedSibling_WithOriginalContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        File.WriteAllText(file, """{ "a": 1 }""");

        var editor = new ClaudeFileEditor(file, () => new DateTimeOffset(2026, 6, 13, 10, 30, 0, TimeSpan.Zero));
        var root = editor.Load();
        root.AsObject()["b"] = 2;
        editor.SaveWithBackup(root);

        var backup = Path.Combine(dir, "settings.json.token-stack-backup-20260613-103000");
        Assert.True(File.Exists(backup));
        Assert.Equal("""{ "a": 1 }""", File.ReadAllText(backup)); // byte-exact original
        Assert.Contains("\"b\"", File.ReadAllText(file));
    }

    [Fact]
    public void Editor_Load_MissingFile_ReturnsEmptyObject()
    {
        var file = Path.Combine(Path.GetTempPath(), "ts-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var editor = new ClaudeFileEditor(file, () => DateTimeOffset.UtcNow);
        Assert.Empty(editor.Load().AsObject());
    }

    // ---------- CCO read-cache hooks ----------

    private const string CcoJs = @"C:\token-stack\cco\src\read-cache.js";

    [Fact]
    public void EnsureCcoHooks_AddsThreeEntries_AcrossEvents()
    {
        var root = Parse(RealisticSettings);
        var changed = ClaudeSurgeon.EnsureCcoHooks(root, CcoJs);
        Assert.True(changed);

        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Contains(pre, e => e!["matcher"]?.GetValue<string>() == "Read"
            && e["hooks"]![0]!["command"]!.GetValue<string>() == $"node \"{CcoJs}\"");
        var post = root["hooks"]!["PostToolUse"]!.AsArray();
        Assert.Contains(post, e => e!["matcher"]?.GetValue<string>() == "Edit|Write"
            && e["hooks"]![0]!["command"]!.GetValue<string>() == $"node \"{CcoJs}\"");
        var pc = root["hooks"]!["PreCompact"]!.AsArray();
        Assert.Single(pc);
        Assert.Equal($"node \"{CcoJs}\"", pc[0]!["hooks"]![0]!["command"]!.GetValue<string>());

        // existing RTK Bash hook is untouched
        Assert.Contains(root["hooks"]!["PreToolUse"]!.AsArray(),
            e => e!["hooks"]![0]!["command"]!.GetValue<string>().Contains("rtk.exe"));
    }

    [Fact]
    public void EnsureCcoHooks_Idempotent()
    {
        var root = Parse(RealisticSettings);
        ClaudeSurgeon.EnsureCcoHooks(root, CcoJs);
        var changed = ClaudeSurgeon.EnsureCcoHooks(root, CcoJs);
        Assert.False(changed);
        Assert.Single(root["hooks"]!["PreCompact"]!.AsArray());
        Assert.Single(root["hooks"]!["PreToolUse"]!.AsArray(),
            e => e!["hooks"]![0]!["command"]!.GetValue<string>().Contains("read-cache.js"));
    }

    [Fact]
    public void EnsureCcoHooks_RewritesStalePath_NoDuplicate()
    {
        var root = Parse(RealisticSettings);
        ClaudeSurgeon.EnsureCcoHooks(root, @"C:\old\read-cache.js");
        var changed = ClaudeSurgeon.EnsureCcoHooks(root, CcoJs);
        Assert.True(changed);
        Assert.Single(root["hooks"]!["PreCompact"]!.AsArray());
        Assert.Equal($"node \"{CcoJs}\"",
            root["hooks"]!["PreCompact"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveCcoHooks_RemovesOnlyOurs()
    {
        var root = Parse(RealisticSettings);
        ClaudeSurgeon.EnsureCcoHooks(root, CcoJs);
        var changed = ClaudeSurgeon.RemoveCcoHooks(root);
        Assert.True(changed);
        Assert.DoesNotContain(root["hooks"]!["PreToolUse"]!.AsArray(),
            e => e!["hooks"]![0]!["command"]!.GetValue<string>().Contains("read-cache.js"));
        Assert.Empty(root["hooks"]!["PreCompact"]!.AsArray());
        // RTK Bash hook survived
        Assert.Contains(root["hooks"]!["PreToolUse"]!.AsArray(),
            e => e!["hooks"]![0]!["command"]!.GetValue<string>().Contains("rtk.exe"));
    }

    [Fact]
    public void EnsureThenRemoveCco_RoundTripsToOriginalHookShape()
    {
        var root = Parse(RealisticSettings);
        ClaudeSurgeon.EnsureCcoHooks(root, CcoJs);
        ClaudeSurgeon.RemoveCcoHooks(root);
        Assert.Single(root["hooks"]!["PreToolUse"]!.AsArray());
        Assert.Empty(root["hooks"]!["PostToolUse"]!.AsArray());
        Assert.Empty(root["hooks"]!["PreCompact"]!.AsArray());
    }
}
