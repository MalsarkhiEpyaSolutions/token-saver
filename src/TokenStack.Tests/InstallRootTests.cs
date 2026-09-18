using TokenStack.Core.Config;
using Xunit;

namespace TokenStack.Tests;

/// <summary>Every test passes an explicit pointer path — the real one lives in the user profile
/// and must never be touched by a test run.</summary>
public class InstallRootTests
{
    private static string TempPointer() => Path.Combine(
        Path.GetTempPath(), "ts-root", Guid.NewGuid().ToString("N"), ".token-saver-root");

    private static string Seed(string contents)
    {
        var p = TempPointer();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, contents);
        return p;
    }

    // ---------- the pointer ----------

    [Fact]
    public void ReadPointer_Absent_IsNull_SoCallersFallBackToLegacyRoot()
        => Assert.Null(ConfigStore.ReadPointer(TempPointer()));

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var p = TempPointer();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        ConfigStore.WritePointer(@"D:\token-stack", p);
        Assert.Equal(@"D:\token-stack", ConfigStore.ReadPointer(p));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("token-stack")]      // relative = meaningless as a root
    [InlineData(@"..\token-stack")]
    public void ReadPointer_RejectsAnythingNotAbsolute(string contents)
        => Assert.Null(ConfigStore.ReadPointer(Seed(contents)));

    [Fact]
    public void ReadPointer_TrimsTrailingNewline()
        => Assert.Equal(@"D:\token-stack", ConfigStore.ReadPointer(Seed("D:\\token-stack\r\n")));

    [Fact]
    public void DeletePointer_IsIdempotent()
    {
        var p = Seed(@"D:\token-stack");
        ConfigStore.DeletePointer(p);
        ConfigStore.DeletePointer(p); // absent now — must not throw
        Assert.Null(ConfigStore.ReadPointer(p));
    }

    [Fact]
    public void LegacyRoot_IsStillTheDefault_WhenNoPointerWasEverWritten()
        => Assert.Equal(@"C:\token-stack", ConfigStore.LegacyRoot);

    // ---------- root validation ----------

    private static List<string> RootErrors(string root) => ConfigValidator
        .Validate(StackConfig.CreateDefault(root))
        .Where(e => e.StartsWith("installRoot", StringComparison.Ordinal)).ToList();

    [Fact]
    public void Validate_AcceptsAnyFixedDriveWithoutSpaces()
    {
        // Every fixed drive on this machine must be a legal root — the whole point of --root is
        // putting a multi-GB stack on the drive that has room for it.
        foreach (var fixedDrive in DriveInfo.GetDrives()
                     .Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            Assert.Empty(RootErrors(Path.Combine(fixedDrive.Name, "token-stack")));
    }

    [Fact]
    public void Validate_RejectsRemovableAndNetworkRoots()
    {
        // A USB or mapped drive installs fine and is dead at the next logon, because the proxy's
        // Scheduled Task fires before it is attached. Refusing up front beats that silence.
        Assert.Contains(RootErrors(@"\\server\share\token-stack"), e => e.Contains("network path"));

        var removable = DriveInfo.GetDrives()
            .FirstOrDefault(d => d.DriveType is DriveType.Removable or DriveType.Network && d.IsReady);
        if (removable is null) return; // none attached right now — nothing to assert
        Assert.NotEmpty(RootErrors(Path.Combine(removable.Name, "token-stack")));
    }

    [Fact]
    public void Validate_RejectsADriveThatIsNotThere()
    {
        // Pick a letter with no volume mounted, so the check is real on any machine. If every
        // letter is somehow taken there is nothing to assert — that is the machine, not a bug.
        var free = "ZYXWV".FirstOrDefault(l => !Directory.Exists($"{l}:\\"));
        if (free == default) return;
        Assert.Contains(RootErrors($@"{free}:\token-stack"), e => e.Contains("does not exist"));
    }

    [Fact]
    public void Validate_RejectsSpacesAndRelativePaths()
    {
        Assert.Contains(RootErrors(@"D:\token stack"), e => e.Contains("spaces"));
        Assert.Contains(RootErrors(@"token-stack"), e => e.Contains("absolute"));
    }
}
