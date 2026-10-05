using TokenStack.Core.Config;
using TokenStack.Core.Install;
using Xunit;

namespace TokenStack.Tests;

/// <summary>Installing to D:\ or E:\ is only useful if EVERY path the installer writes follows
/// the chosen root. A single leftover hard-coded C:\token-stack would produce a stack that
/// installs onto one drive and is wired to another — working on the developer's machine, broken
/// on anyone who moved it. This drives the real wiring entry point and asserts nothing points at
/// the default root.</summary>
[Collection(MsixProbe.Name)] // shares the pipeline's fixed-path probe
public class NonDefaultRootWiringTests
{
    private const string Root = @"E:\token-stack";

    private static (string settings, string claudeJson) Wire(StackConfig cfg)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-root-wire", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = Path.Combine(dir, "settings.json");
        var claudeJson = Path.Combine(dir, ".claude.json");
        File.WriteAllText(settings, "{}");
        File.WriteAllText(claudeJson, "{}");

        new InstallPipeline(new FakeRunner(), new FakeEnv(), new FakePort(), new FakeHttp(), _ => { })
        {
            SettingsPath = settings,
            ClaudeJsonPath = claudeJson,
        }.ApplyClaudeWiring(cfg);

        return (File.ReadAllText(settings), File.ReadAllText(claudeJson));
    }

    [Fact]
    public void EveryWiredPathFollowsTheChosenRoot()
    {
        var cfg = StackConfig.CreateDefault(Root);
        var (settings, _) = Wire(cfg);

        // The rtk hook is the bare spelling, so it carries no path at all — rtk's binding to the
        // chosen root lives in the USER PATH entry RtkComponent.Install writes, not here. Every
        // path that IS written to settings.json still has to follow E:.
        Assert.Contains("rtk hook claude", settings);
        Assert.Contains(@"E:\\token-stack\\cco\\src\\read-cache.js", settings);
        Assert.Contains(@"E:\\token-stack\\token-saver.exe", settings);   // session status hook

        // The one thing that must NOT appear anywhere.
        Assert.DoesNotContain("token-stack\\\\config.json", settings);
        Assert.DoesNotContain(@"C:\\token-stack", settings);
    }

    [Fact]
    public void DisabledLayersLeaveNoRootPathsBehind()
    {
        var cfg = StackConfig.CreateDefault(Root);
        cfg.Rtk.Enabled = cfg.Cco.Enabled = false;
        var (settings, _) = Wire(cfg);

        Assert.DoesNotContain("hook claude", settings);  // catches BOTH spellings, not just the path one
        Assert.DoesNotContain("read-cache.js", settings);
    }

    /// <summary>The config file and the pointer are the pair that makes a moved install
    /// findable again: config.json belongs inside the root, the pointer outside it.</summary>
    [Fact]
    public void ConfigBelongsInsideTheRoot_PointerOutsideIt()
    {
        Assert.Equal(Path.Combine(Root, "config.json"),
                     Path.Combine(StackConfig.CreateDefault(Root).InstallRoot, "config.json"));

        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ConfigStore.DefaultPointerPath);
        Assert.DoesNotContain("token-stack", ConfigStore.DefaultPointerPath);
    }
}
