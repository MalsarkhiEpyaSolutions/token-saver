using TokenStack.Core.Config;
using TokenStack.Core.Install;
using Xunit;

namespace TokenStack.Tests;

public class InstallStateTests
{
    private const string Setup = "1.5.0";

    [Fact]
    public void NoRoot_IsAFirstInstall()
    {
        var s = InstallState.Compare(null, null, Setup);
        Assert.Equal(InstallVerdict.NotInstalled, s.Verdict);
        Assert.False(s.AlreadyCurrent);
    }

    [Theory]
    [InlineData("1.4.0")]
    [InlineData("1.0.4")]
    [InlineData("0.9.9")]
    public void OlderInstall_OffersAnUpdate(string installed)
        => Assert.Equal(InstallVerdict.UpdateAvailable,
                        InstallState.Compare(@"C:\token-stack", installed, Setup).Verdict);

    [Fact]
    public void SameVersion_IsCurrent()
    {
        var s = InstallState.Compare(@"C:\token-stack", "1.5.0", Setup);
        Assert.Equal(InstallVerdict.UpToDate, s.Verdict);
        Assert.True(s.AlreadyCurrent);
    }

    /// <summary>Running an older setup over a newer install must not offer a "downgrade" —
    /// there is nothing to update, and silently rolling a working stack back is worse than
    /// doing nothing.</summary>
    [Fact]
    public void NewerInstall_IsLeftAlone()
        => Assert.Equal(InstallVerdict.UpToDate,
                        InstallState.Compare(@"C:\token-stack", "2.0.0", "1.5.0").Verdict);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void InstallWithNoUsableVersion_IsUpgradable_NotAssumedCurrent(string? installed)
        => Assert.Equal(InstallVerdict.UnknownVersion,
                        InstallState.Compare(@"C:\token-stack", installed, Setup).Verdict);

    [Fact]
    public void BuildMetadataIsIgnored()
        => Assert.Equal(InstallVerdict.UpToDate,
                        InstallState.Compare(@"C:\ts", "1.5.0+abc1234", "1.5.0").Verdict);

    /// <summary>An unreadable version on OUR side is our bug; never tell the user their install
    /// is stale because of it.</summary>
    [Fact]
    public void UnparsableSetupVersion_NeverClaimsStale()
        => Assert.Equal(InstallVerdict.UnknownVersion,
                        InstallState.Compare(@"C:\ts", "1.4.0", "garbage").Verdict);

    // ---------- the I/O wrapper ----------

    [Fact]
    public void Inspect_MissingConfig_IsNotInstalled()
    {
        var path = Path.Combine(Path.GetTempPath(), "ts-state", Guid.NewGuid().ToString("N"), "config.json");
        Assert.Equal(InstallVerdict.NotInstalled, InstallState.Inspect(Setup, path).Verdict);
    }

    [Fact]
    public void Inspect_CorruptConfig_IsNotInstalled_RatherThanThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, "{ this is not json");
        Assert.Equal(InstallVerdict.NotInstalled, InstallState.Inspect(Setup, path).Verdict);
    }

    [Fact]
    public void Inspect_ReadsRootAndVersionFromConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");

        var cfg = StackConfig.CreateDefault(@"D:\token-stack");
        cfg.Version = "1.4.0";
        ConfigStore.Save(cfg, path);

        var s = InstallState.Inspect(Setup, path);
        Assert.Equal(InstallVerdict.UpdateAvailable, s.Verdict);
        Assert.Equal(@"D:\token-stack", s.Root);
        Assert.Equal("1.4.0", s.InstalledVersion);
    }
}
