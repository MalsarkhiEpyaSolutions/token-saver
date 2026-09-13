using TokenStack.Core.Config;
using TokenStack.Core.Install;
using Xunit;

namespace TokenStack.Tests;

public class PurgeTests
{
    /// <summary>--purge must cover exactly the two dirs the installer creates, and must NOT list
    /// the HuggingFace cache or ~/.local — those are shared with other tooling.</summary>
    [Fact]
    public void PurgePaths_CoversInstallRootAndReadCache_ButNothingShared()
    {
        var cfg = new StackConfig { InstallRoot = @"C:\token-stack" };
        var paths = InstallPipeline.PurgePaths(cfg).ToList();

        Assert.Contains(@"C:\token-stack", paths);
        Assert.Contains(paths, p => p.EndsWith(".claude-context-optimizer", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("huggingface", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.EndsWith(@".local\bin", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, paths.Count);
    }
}
