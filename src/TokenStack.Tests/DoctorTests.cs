using System.Text.Json.Nodes;
using TokenStack.Core.Claude;
using TokenStack.Core.Config;
using TokenStack.Core.Doctor;
using Xunit;

namespace TokenStack.Tests;

public class DoctorTests
{
    private static DoctorContext Ctx(
        Action<FakeEnv>? env = null, bool portListening = true,
        string settings = "{}", string claudeJson = "{}",
        StackConfig? cfg = null, FakeRunner? runner = null)
    {
        var e = new FakeEnv();
        env?.Invoke(e);
        return new DoctorContext(
            cfg ?? StackConfig.CreateDefault(@"C:\ts"),
            JsonNode.Parse(settings)!, JsonNode.Parse(claudeJson)!,
            e, new FakePort { Listening = portListening },
            runner ?? new FakeRunner(), new FakeHttp());
    }

    private const string TwoRtkHooks = """
    {"hooks":{"PreToolUse":[
      {"matcher":"Bash","hooks":[{"type":"command","command":"rtk hook claude"}]},
      {"matcher":"Bash","hooks":[{"type":"command","command":"\"C:\\ts\\rtk\\rtk.exe\" hook claude"}]}
    ]}}
    """;

    [Fact]
    public void RtkHookDuplicate_Detected_AndCollapsedByFix()
    {
        var ctx = Ctx(settings: TwoRtkHooks);
        var check = new RtkHookDuplicateCheck();

        var before = check.Detect(ctx);
        Assert.False(before.Ok);
        Assert.Contains("runs 2x", before.Detail);
        Assert.True(before.CanFix);

        Assert.True(check.Fix(ctx));
        Assert.True(check.Detect(ctx).Ok);
    }

    [Fact]
    public void RtkHookDuplicate_OkForTheSingleHookTheInstallerWrites()
    {
        var ctx = Ctx(settings: """
        {"hooks":{"PreToolUse":[
          {"matcher":"Bash","hooks":[{"type":"command","command":"\"C:\\ts\\rtk\\rtk.exe\" hook claude"}]}
        ]}}
        """);
        Assert.True(new RtkHookDuplicateCheck().Detect(ctx).Ok);
    }

    /// <summary>A hand-added bare hook filters perfectly — reporting it as "absent" sent at least
    /// one session chasing a bug that was not there.</summary>
    [Fact]
    public void RtkHookMissing_AcceptsTheBareSpelling()
    {
        var ctx = Ctx(settings: """
        {"hooks":{"PreToolUse":[
          {"matcher":"Bash","hooks":[{"type":"command","command":"rtk hook claude"}]}
        ]}}
        """);
        var r = new RtkHookMissingCheck().Detect(ctx);
        Assert.True(r.Ok);
        Assert.Contains("PATH", r.Detail);
    }

    private const string BareRtkHook = """
    {"hooks":{"PreToolUse":[
      {"matcher":"Bash","hooks":[{"type":"command","command":"rtk hook claude"}]}
    ]}}
    """;

    /// <summary>Lays out a real rtk.exe under <paramref name="root"/> and, when
    /// <paramref name="shadowDir"/> is given, a second one in a dir that precedes it on PATH.
    /// Real files because the resolver probes the filesystem the way Windows does.</summary>
    private static DoctorContext PathCtx(out string root, string? shadowDir = null,
                                         string settings = BareRtkHook)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ts-path", Guid.NewGuid().ToString("N"));
        root = Path.Combine(tmp, "ts");
        var ourDir = Path.Combine(root, "rtk");
        Directory.CreateDirectory(ourDir);
        File.WriteAllText(Path.Combine(ourDir, "rtk.exe"), "");

        var machine = "";
        if (shadowDir is not null)
        {
            machine = Path.Combine(tmp, shadowDir);
            Directory.CreateDirectory(machine);
            File.WriteAllText(Path.Combine(machine, "rtk.exe"), "");
        }
        var r = root;
        return Ctx(e =>
        {
            e.Process["Path"] = machine;          // MACHINE PATH is searched first
            e.User["Path"] = Path.Combine(r, "rtk");
            e.Process["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        }, settings: settings, cfg: StackConfig.CreateDefault(root));
    }

    /// <summary>The dangerous mode: a different rtk earlier on PATH is handed every Bash command
    /// and returns the rewrite Claude executes. `rtk --version` succeeding cannot distinguish
    /// this from a healthy install, which is why the check compares resolved paths.</summary>
    [Fact]
    public void RtkHookUnresolvable_FlagsAShadowingRtk_AndPinsTheFullPath()
    {
        var ctx = PathCtx(out var root, shadowDir: "evil");
        var check = new RtkHookUnresolvableCheck();

        var bad = check.Detect(ctx);
        Assert.False(bad.Ok);
        Assert.True(bad.CanFix);
        Assert.Contains("shadows", bad.Detail);
        Assert.Contains("evil", bad.Detail);        // names the actual winner

        Assert.True(check.Fix(ctx));
        Assert.Contains(Path.Combine(root, "rtk", "rtk.exe"),
            ctx.Settings["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.True(check.Detect(ctx).Ok);          // a pinned path needs no PATH lookup
        Assert.Equal(1, ClaudeSurgeon.CountRtkHooks(ctx.Settings));
    }

    /// <summary>The silent mode: nothing resolves, so Claude cannot run the hook and filtering
    /// stops with nothing printed anywhere.</summary>
    [Fact]
    public void RtkHookUnresolvable_CatchesTheSilentFailure_WhenNothingResolves()
    {
        var ctx = Ctx(settings: BareRtkHook);      // FakeEnv has no PATH at all
        var bad = new RtkHookUnresolvableCheck().Detect(ctx);
        Assert.False(bad.Ok);
        Assert.Contains("silently off", bad.Detail);
    }

    [Fact]
    public void RtkHookUnresolvable_QuietWhenBareResolvesToOurs_OrNothingIsWired()
    {
        var check = new RtkHookUnresolvableCheck();
        Assert.True(check.Detect(PathCtx(out _)).Ok);
        // an absent hook belongs to rtk-hook-missing; two checks shouting about it is noise
        Assert.True(check.Detect(Ctx()).Ok);
    }

    /// <summary>Collapsing a duplicate must not switch a machine whose PATH is unsafe over to the
    /// bare form — that would trade a visible warning for a substitutable command.</summary>
    [Fact]
    public void RtkHookDuplicate_CollapsesToFullPath_WhenPathIsUnsafe()
    {
        foreach (var ctx in new[] { PathCtx(out _, "evil", TwoRtkHooks),   // shadowed
                                    Ctx(settings: TwoRtkHooks) })         // nothing resolves
        {
            Assert.True(new RtkHookDuplicateCheck().Fix(ctx));
            Assert.Equal(1, ClaudeSurgeon.CountRtkHooks(ctx.Settings));
            Assert.Contains("rtk.exe",
                ctx.Settings["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        }
    }

    [Fact]
    public void RtkHookDuplicate_CollapsesToBare_WhenPathResolvesToOurs()
    {
        var ctx = PathCtx(out _, settings: TwoRtkHooks);
        Assert.True(new RtkHookDuplicateCheck().Fix(ctx));
        Assert.Equal("rtk hook claude",
            ctx.Settings["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void RtkHookMissing_NamesWhichFormIsWired()
    {
        Assert.Contains("PATH", new RtkHookMissingCheck().Detect(Ctx(settings: BareRtkHook)).Detail);
        var full = Ctx(settings: """
        {"hooks":{"PreToolUse":[
          {"matcher":"Bash","hooks":[{"type":"command","command":"\"C:\\ts\\rtk\\rtk.exe\" hook claude"}]}
        ]}}
        """);
        var r = new RtkHookMissingCheck().Detect(full);
        Assert.True(r.Ok);
        Assert.Contains("fallback", r.Detail);
    }

    [Fact]
    public void DisabledDrift_SeesABareHookToo()
    {
        var cfg = StackConfig.CreateDefault(@"C:\ts");
        cfg.Rtk.Enabled = false;
        Assert.False(new DisabledDriftCheck().Detect(Ctx(cfg: cfg, settings: BareRtkHook)).Ok);
    }

    [Fact]
    public void RipgrepMissing_FollowsTheRunner_AndIsNotAutoFixable()
    {
        var absent = Ctx(runner: new FakeRunner { Handler = (_, _) => new(1, "", "not found") });
        var present = Ctx(runner: new FakeRunner { Handler = (_, _) => new(0, "ripgrep 14.1.0", "") });
        var check = new RipgrepMissingCheck();

        var bad = check.Detect(absent);
        Assert.False(bad.Ok);
        Assert.False(bad.CanFix);                 // we do not install packages behind the user
        Assert.Contains("winget", bad.Detail);    // but we name the exact command
        Assert.False(check.Fix(absent));

        Assert.True(check.Detect(present).Ok);
    }

    [Fact]
    public void RtkChecks_StayQuiet_WhenRtkIsDisabled()
    {
        var cfg = StackConfig.CreateDefault(@"C:\ts");
        cfg.Rtk.Enabled = false;
        var ctx = Ctx(settings: TwoRtkHooks, cfg: cfg,
                      runner: new FakeRunner { Handler = (_, _) => new(1, "", "not found") });

        Assert.True(new RtkHookDuplicateCheck().Detect(ctx).Ok);
        Assert.True(new RipgrepMissingCheck().Detect(ctx).Ok);
        Assert.True(new RtkHookUnresolvableCheck().Detect(ctx).Ok);
    }

    [Fact]
    public void RoutingBypassed_Detected_AndFixed()
    {
        var ctx = Ctx(e =>
        {
            e.User["ANTHROPIC_BASE_URL"] = "https://api.anthropic.com"; // wrong scope value
            e.Process["ANTHROPIC_BASE_URL"] = "https://api.anthropic.com";
        });
        var check = new RoutingBypassedCheck();
        Assert.False(check.Detect(ctx).Ok);
        Assert.True(check.Fix(ctx));
        Assert.Equal("http://127.0.0.1:8787",
            ((FakeEnv)ctx.Env).User["ANTHROPIC_BASE_URL"]);
    }

    [Fact]
    public void ProxyZombie_Detected_WhenTaskRunningPortDead()
    {
        var runner = new FakeRunner
        {
            Handler = (f, a) => a.Contains("/query") && a.Contains("/fo csv")
                ? new(0, "\"HeadroomProxy\",\"N/A\",\"Running\"", "")
                : new(0, "", ""),
        };
        var ctx = Ctx(portListening: false, runner: runner);
        Assert.False(new ProxyZombieCheck().Detect(ctx).Ok);
    }

    [Fact]
    public void SembleUvx_Detected_WhenCommandIsUvx()
    {
        var ctx = Ctx(claudeJson:
            """{ "mcpServers": { "semble": { "command": "uvx", "args": ["--from","semble[mcp]","semble"] } } }""");
        Assert.False(new SembleUvxCheck().Detect(ctx).Ok);
    }

    [Fact]
    public void RtkHookMissing_Detected_OnEmptySettings()
    {
        Assert.False(new RtkHookMissingCheck().Detect(Ctx()).Ok);
    }

    [Fact]
    public void RtkHookPowershell_Detected()
    {
        var ctx = Ctx(settings:
            """{ "hooks": { "PreToolUse": [ { "matcher": "PowerShell", "hooks": [ { "type":"command","command":"\"C:\\ts\\rtk\\rtk.exe\" hook claude" } ] } ] } }""");
        Assert.False(new RtkHookPowershellCheck().Detect(ctx).Ok);
    }

    [Fact]
    public void ModelPin_DetectedAndFixed()
    {
        var ctx = Ctx(e => e.User["ANTHROPIC_MODEL"] = "claude-opus-4-6");
        var check = new ModelPinLeftoverCheck();
        Assert.False(check.Detect(ctx).Ok);
        Assert.True(check.Fix(ctx));
        Assert.False(((FakeEnv)ctx.Env).User.ContainsKey("ANTHROPIC_MODEL"));
    }

    [Fact]
    public void PathSpaces_Detected()
    {
        var ctx = Ctx(cfg: StackConfig.CreateDefault(@"C:\proxy tokens"));
        var r = new PathSpacesCheck().Detect(ctx);
        Assert.False(r.Ok);
        Assert.False(r.CanFix); // guided reinstall, not auto-fix
    }

    [Fact]
    public void DisabledDrift_Detected_WhenRtkDisabledButHookPresent()
    {
        var cfg = StackConfig.CreateDefault(@"C:\ts");
        cfg.Rtk.Enabled = false;
        var ctx = Ctx(cfg: cfg, settings:
            """{ "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type":"command","command":"\"C:\\ts\\rtk\\rtk.exe\" hook claude" } ] } ] } }""");
        Assert.False(new DisabledDriftCheck().Detect(ctx).Ok);
    }

    [Fact]
    public void OfflineModels_Ok_WhenNoHfCache_OnlineInstall()
    {
        // C:\ts has no hf-cache dir → online install → N/A → ok
        Assert.True(new OfflineModelsPresentCheck().Detect(Ctx()).Ok);
    }

    [Fact]
    public void OfflineModels_Fails_WhenHfCachePresentButEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "ts-doc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "hf-cache")); // present but no models
        var ctx = Ctx(cfg: StackConfig.CreateDefault(root));
        Assert.False(new OfflineModelsPresentCheck().Detect(ctx).Ok);
    }

    [Fact]
    public void OfflineModels_Ok_WhenModelsPresent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ts-doc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "hf-cache", "hub", "models--answerdotai--ModernBERT-base"));
        var ctx = Ctx(cfg: StackConfig.CreateDefault(root));
        Assert.True(new OfflineModelsPresentCheck().Detect(ctx).Ok);
    }

    [Fact]
    public void Registry_ContainsEveryCheckInOrder()
    {
        Assert.Equal(new[]
        {
            "routing-bypassed", "proxy-zombie", "proxy-extra-missing", "semble-uvx",
            "rtk-hook-missing", "rtk-hook-powershell", "rtk-hook-duplicate",
            "rtk-hook-unresolvable", "ripgrep-missing",
            "cco-hook-missing", "model-pin-leftover",
            "path-spaces", "task-misconfigured", "stack-disabled-drift", "offline-models-present",
        }, DoctorRegistry.All.Select(c => c.Id).ToArray());
        Assert.Equal(15, DoctorRegistry.All.Count);
    }

    [Fact]
    public void CcoHookMissingCheck_FailsThenFixes_WhenEnabledButUnwired()
    {
        var cfg = StackConfig.CreateDefault(@"C:\ts"); // cco enabled
        var ctx = new DoctorContext(cfg, JsonNode.Parse("{}")!, JsonNode.Parse("{}")!,
            new FakeEnv(), new FakePort(), new FakeRunner(), new FakeHttp());

        var check = new CcoHookMissingCheck();
        Assert.False(check.Detect(ctx).Ok);      // enabled but no hook present
        Assert.True(check.Fix(ctx));             // adds it
        Assert.True(ctx.SettingsChanged);
        Assert.True(check.Detect(ctx).Ok);       // now present
    }
}
