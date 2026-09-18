using TokenStack.Core.Install;
using Xunit;

namespace TokenStack.Tests;

/// <summary>The MSIX guard probes a FIXED, shared location (%LOCALAPPDATA%), so any two tests
/// that trigger it race: one asserts the directory holds no probe while the other still has its
/// own probe in flight, and the assertion fails at random. Every test that reaches the guard —
/// directly or through Preflight — joins this collection so they run one at a time.</summary>
[CollectionDefinition(MsixProbe.Name, DisableParallelization = true)]
public class MsixProbe
{
    public const string Name = "msix-probe";
}

[Collection(MsixProbe.Name)]
public class MsixGuardTests
{
    /// <summary>The test host is not packaged, so the guard must pass — and must not leave its
    /// probe behind in %LOCALAPPDATA% (it runs on every install).</summary>
    [Fact]
    public void Guard_Passes_AndLeavesNoProbe_OnAnUnpackagedProcess()
    {
        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");

        InstallPipeline.GuardAgainstMsixVirtualization();

        Assert.Empty(Directory.GetFiles(localAppData, ".ts-virt-probe-*"));
    }
}
