using TokenStack.Core.Components;
using TokenStack.Core.Config;
using Xunit;

namespace TokenStack.Tests;

public class ToggleServiceTests
{
    private static (string settings, string claudeJson) TempFiles(string settings, string claudeJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-toggle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sp = Path.Combine(dir, "settings.json");
        var cj = Path.Combine(dir, ".claude.json");
        File.WriteAllText(sp, settings);
        File.WriteAllText(cj, claudeJson);
        return (sp, cj);
    }

    private static FakeRunner TaskRunningRunner() => new()
    {
        Handler = (_, a) => a.Contains("/query") && a.Contains("/fo csv")
            ? new(0, "\"HeadroomProxy\",\"N/A\",\"Running\"", "")
            : new(0, "", ""),
    };

    [Fact]
    public void ApplyWiring_HeadroomOff_RemovesRouting_AndStopsTask()
    {
        var env = new FakeEnv();
        env.User["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:8787";
        var runner = TaskRunningRunner();
        var (sp, cj) = TempFiles(
            """{ "env": { "ANTHROPIC_BASE_URL": "http://127.0.0.1:8787" } }""", "{}");
        var cfg = StackConfig.CreateDefault(@"C:\ts");
        cfg.Headroom.Enabled = false;

        var state = new ToggleService(runner, env, sp, cj).ApplyWiring(cfg);

        Assert.False(state.Headroom);
        Assert.False(env.User.ContainsKey("ANTHROPIC_BASE_URL"));   // desktop routing removed
        Assert.Contains(runner.Calls, c => c.Contains("/end"));      // proxy task stopped
        Assert.Contains(runner.Calls, c => c.Contains("/disable")); // and won't relaunch at next logon
        Assert.DoesNotContain("ANTHROPIC_BASE_URL", File.ReadAllText(sp)); // settings.json routing removed
    }

    [Fact]
    public void ApplyWiring_RtkOff_RemovesHook_LeavesOthers()
    {
        var (sp, cj) = TempFiles(
            """{ "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type":"command","command":"\"C:\\ts\\rtk\\rtk.exe\" hook claude" } ] } ] } }""",
            "{}");
        var cfg = StackConfig.CreateDefault(@"C:\ts");
        cfg.Rtk.Enabled = false;

        new ToggleService(new FakeRunner(), new FakeEnv(), sp, cj).ApplyWiring(cfg);

        // "hook claude" catches BOTH spellings — asserting only on "rtk.exe" would now pass
        // trivially even if a bare hook had survived removal.
        Assert.DoesNotContain("hook claude", File.ReadAllText(sp));
    }

    [Fact]
    public void ApplyWiring_SembleOff_RemovesMcp_LeavesOtherServers()
    {
        var (sp, cj) = TempFiles("{}",
            """{ "mcpServers": { "semble": { "command": "x" }, "playwright": { "command": "npx" } } }""");
        var cfg = StackConfig.CreateDefault(@"C:\ts");
        cfg.Semble.Enabled = false;

        new ToggleService(new FakeRunner(), new FakeEnv(), sp, cj).ApplyWiring(cfg);

        var c = File.ReadAllText(cj);
        Assert.DoesNotContain("\"semble\"", c);
        Assert.Contains("playwright", c);
    }

    [Fact]
    public void ApplyWiring_AllOn_WiresEverything()
    {
        var env = new FakeEnv();
        var runner = TaskRunningRunner();
        var (sp, cj) = TempFiles("{}", "{}");
        var cfg = StackConfig.CreateDefault(@"C:\ts"); // all enabled

        var st = new ToggleService(runner, env, sp, cj).ApplyWiring(cfg);

        Assert.True(st is { Headroom: true, Rtk: true, Semble: true });
        Assert.Contains(runner.Calls, c => c.Contains("/enable")); // re-armed after a prior off
        Assert.Equal("http://127.0.0.1:8787", env.User["ANTHROPIC_BASE_URL"]);
        // Spelling-agnostic: this test asks "did everything get wired", and which rtk spelling is
        // written depends on whether PATH provably resolves to our exe (it cannot here).
        Assert.Contains("hook claude", File.ReadAllText(sp));
        Assert.Contains("semble", File.ReadAllText(cj));
    }

    [Fact]
    public void ApplyWiring_CcoOn_AddsReadCacheHook()
    {
        var (sp, cj) = TempFiles("{}", "{}");
        var cfg = StackConfig.CreateDefault(@"C:\ts"); // cco enabled by default

        var st = new ToggleService(new FakeRunner(), new FakeEnv(), sp, cj).ApplyWiring(cfg);

        Assert.True(st.Cco);
        var s = File.ReadAllText(sp);
        Assert.Contains("read-cache.js", s);
        Assert.Contains("\"node ", s);       // command value begins with a bare `node …`
        Assert.DoesNotContain("test -f", s); // not the POSIX-wrapped hooks.json form
    }

    [Fact]
    public void ApplyWiring_CcoOff_RemovesReadCacheHook_LeavesRtk()
    {
        var (sp, cj) = TempFiles(
            """{ "hooks": { "PreToolUse": [ { "matcher":"Read","hooks":[{"type":"command","command":"node \"C:\\ts\\cco\\src\\read-cache.js\""}] }, { "matcher":"Bash","hooks":[{"type":"command","command":"\"C:\\ts\\rtk\\rtk.exe\" hook claude"}] } ] } }""",
            "{}");
        var cfg = StackConfig.CreateDefault(@"C:\ts");
        cfg.Cco.Enabled = false;

        var st = new ToggleService(new FakeRunner(), new FakeEnv(), sp, cj).ApplyWiring(cfg);

        Assert.False(st.Cco);
        var s = File.ReadAllText(sp);
        Assert.DoesNotContain("read-cache.js", s);
        Assert.Contains("hook claude", s); // RTK survived (either spelling)
    }
}
