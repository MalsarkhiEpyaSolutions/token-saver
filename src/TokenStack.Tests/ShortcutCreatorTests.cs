using TokenStack.Core.Components;
using TokenStack.Core.Windows;
using Xunit;

namespace TokenStack.Tests;

public class ShortcutCreatorTests
{
    [Fact]
    public void LayerSpecs_CoverEveryToggleableLayer()
    {
        var specs = ShortcutCreator.LayerSpecs();
        Assert.Contains(specs, s => s.Arguments == "toggle headroom --notify");
        Assert.Contains(specs, s => s.Arguments == "toggle rtk --notify");
        Assert.Contains(specs, s => s.Arguments == "toggle semble --notify");
        Assert.Contains(specs, s => s.Arguments == "toggle cco --notify");
        Assert.All(specs, s => Assert.EndsWith(".lnk", s.FileName));
    }

    /// <summary>A layer you can `off` from the CLI but cannot reach from the Controls folder is
    /// the bug this catches — CCO shipped that way until v1.3.1.</summary>
    [Fact]
    public void EveryToggleableLayer_HasAButton()
    {
        var args = ShortcutCreator.LayerSpecs().Select(s => s.Arguments).ToList();
        foreach (var layer in Enum.GetValues<StackLayer>().Where(l => l != StackLayer.All))
            Assert.Contains(args, a => a == $"toggle {layer.ToString().ToLowerInvariant()} --notify");
    }

    [Fact]
    public void Toggles_AreMinimized_ButUninstallIsVisible()
    {
        // A minimized window would hide uninstall's confirmation prompt.
        Assert.All(ShortcutCreator.LayerSpecs(), s => Assert.Equal(7, s.WindowStyle));
        Assert.Equal(1, ShortcutCreator.UninstallSpec().WindowStyle);
    }

    [Fact]
    public void UninstallButton_NeverPurgesAndNeverSkipsTheConfirmation()
    {
        var args = ShortcutCreator.UninstallSpec().Arguments;
        Assert.Equal("uninstall", args);          // exact: one mis-click must stay reversible
        Assert.DoesNotContain("--purge", args);
        Assert.DoesNotContain("-y", args);
    }

    [Fact]
    public void Locations_AreOnTheDesktop()
    {
        Assert.EndsWith("Token Stack.lnk", ShortcutCreator.WholeStackPath);      // loose quick button
        Assert.EndsWith("Token Stack Controls", ShortcutCreator.ControlsFolder); // folder for the 3
        Assert.Contains("Desktop", ShortcutCreator.WholeStackPath);
        Assert.Contains("Desktop", ShortcutCreator.ControlsFolder);
    }
}
