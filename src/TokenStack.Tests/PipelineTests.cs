using TokenStack.Core.Config;
using TokenStack.Core.Install;
using Xunit;

namespace TokenStack.Tests;

[Collection(MsixProbe.Name)] // Preflight runs the MSIX guard — see MsixProbe
public class PipelineTests
{
    private static InstallPipeline PipeWith(string settings, string claudeJson, out FakeEnv env)
    {
        env = new FakeEnv();
        return new InstallPipeline(new FakeRunner(), env, new FakePort(), new FakeHttp(), _ => { })
        {
            SettingsPath = settings,
            ClaudeJsonPath = claudeJson,
        };
    }

    private static string SeedSettings(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-det", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = Path.Combine(dir, "settings.json");
        File.WriteAllText(settings, json);
        return settings;
    }

    [Fact]
    public void DetectUpstream_AdoptsVendorBaseUrlFromSettings()
    {
        var settings = SeedSettings("""{"env":{"ANTHROPIC_BASE_URL":"https://api.moonshot.ai/anthropic"}}""");
        var pipe = PipeWith(settings, settings + ".claude.json", out _);
        var cfg = StackConfig.CreateDefault(@"C:\ts");

        pipe.DetectUpstream(cfg);

        Assert.Equal("https://api.moonshot.ai/anthropic", cfg.Headroom.UpstreamUrl);
    }

    [Fact]
    public void DetectUpstream_PlainAnthropic_LeavesEmpty()
    {
        var settings = SeedSettings("""{"env":{"ANTHROPIC_BASE_URL":"https://api.anthropic.com"}}""");
        var pipe = PipeWith(settings, settings + ".claude.json", out _);
        var cfg = StackConfig.CreateDefault(@"C:\ts");

        pipe.DetectUpstream(cfg);

        Assert.Equal("", cfg.Headroom.UpstreamUrl);
    }

    [Fact]
    public void PlanSteps_FollowSpecOrder_AndHonorEnabledFlags()
    {
        var cfg = StackConfig.CreateDefault(@"C:\ts");
        var all = InstallPipeline.PlanSteps(cfg).Select(s => s.Name).ToArray();
        Assert.Equal(new[]
        {
            "preflight", "bootstrap-uv", "headroom", "rtk", "semble", "cco", "routing", "hooks", "save-config",
        }, all);

        cfg.Rtk.Enabled = false;
        cfg.Semble.Enabled = false;
        var subset = InstallPipeline.PlanSteps(cfg).Select(s => s.Name).ToArray();
        Assert.DoesNotContain("rtk", subset);
        Assert.DoesNotContain("semble", subset);
    }

    [Fact]
    public void Preflight_RejectsSpacesInRoot()
    {
        var cfg = StackConfig.CreateDefault(@"C:\proxy tokens");
        var ex = Record.Exception(() => InstallPipeline.Preflight(cfg));
        Assert.NotNull(ex);
        Assert.Contains("spaces", ex!.Message);
    }

    [Fact]
    public void Preflight_AcceptsCleanRoot()
    {
        // Non-profile root: profile paths can be MSIX-virtualized when tests run inside a
        // packaged host, which the guard (correctly) rejects.
        var root = @"C:\ts-test-" + Guid.NewGuid().ToString("N");
        var cfg = StackConfig.CreateDefault(root);
        try { InstallPipeline.Preflight(cfg); /* must not throw */ }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PlanSteps_IncludesCco_WhenEnabled_AndOmits_WhenDisabled()
    {
        var on = StackConfig.CreateDefault(@"C:\ts");
        Assert.Contains(InstallPipeline.PlanSteps(on), s => s.Name == "cco");

        var off = StackConfig.CreateDefault(@"C:\ts");
        off.Cco.Enabled = false;
        Assert.DoesNotContain(InstallPipeline.PlanSteps(off), s => s.Name == "cco");
    }
}
