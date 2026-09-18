using TokenStack.Core.Config;
using TokenStack.Core.Install;
using Xunit;

namespace TokenStack.Tests;

public class StepProgressTests
{
    [Theory]
    [InlineData("[1/8] preflight", 1, 8)]
    [InlineData("[8/8] save config", 8, 8)]
    [InlineData("      [3/8] indented", 3, 8)]
    public void Parse_ReadsTheStepPrefix(string line, int step, int total)
        => Assert.Equal((step, total), StepProgress.Parse(line));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("      ready on :8787")]           // a plain sub-step line
    [InlineData("[cco] read-cache 4.6.0")]         // the un-numbered cco line
    [InlineData("DONE. Fully quit Claude Desktop")]
    [InlineData("[9/8] impossible")]               // past the end = misread, not progress
    [InlineData("[1/0] no total")]
    public void Parse_ReturnsNull_RatherThanGuessing(string? line)
        => Assert.Null(StepProgress.Parse(line));

    [Fact]
    public void Percent_ClampsAndRounds()
    {
        Assert.Equal(0, StepProgress.Percent(0, 8));
        Assert.Equal(50, StepProgress.Percent(4, 8));
        Assert.Equal(100, StepProgress.Percent(8, 8));
        Assert.Equal(0, StepProgress.Percent(1, 0));   // never divide by zero
        Assert.Equal(100, StepProgress.Percent(99, 8)); // clamped, not 1237%
    }

    /// <summary>PlanSteps is NOT where the `[n/8]` labels come from — those are literals in
    /// Run(), and cco is logged as an un-numbered `[cco]` line, so the plan legitimately holds
    /// one more entry than the labels count. Asserting they match would be asserting a
    /// coincidence. What the GUI actually relies on is the parser failing soft, which the
    /// Parse_ReturnsNull cases above cover: an unrecognised line leaves the bar where it is.</summary>
    [Fact]
    public void PlanSteps_CoversEveryEnabledLayer()
    {
        var all = StackConfig.CreateDefault(@"C:\ts");
        var names = InstallPipeline.PlanSteps(all).Select(s => s.Name).ToList();
        Assert.Equal(new[] { "preflight", "bootstrap-uv", "headroom", "rtk", "semble", "cco",
                             "routing", "hooks", "save-config" }, names);

        var bare = StackConfig.CreateDefault(@"C:\ts");
        bare.Headroom.Enabled = bare.Rtk.Enabled = bare.Semble.Enabled = bare.Cco.Enabled = false;
        Assert.DoesNotContain("headroom", InstallPipeline.PlanSteps(bare).Select(s => s.Name));
    }
}
