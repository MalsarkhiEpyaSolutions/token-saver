using TokenStack.Core.Install;
using Xunit;

namespace TokenStack.Tests;

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
