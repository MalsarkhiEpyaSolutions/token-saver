using TokenStack.Core.Windows;
using Xunit;

namespace TokenStack.Tests;

/// <summary>The bare hook spelling is only safe if we can say WHICH rtk it runs. These pin the
/// resolution rules the safety gate depends on.</summary>
public class PathResolverTests
{
    private const string Ext = ".COM;.EXE;.BAT;.CMD";

    private static Func<string, bool> Present(params string[] files) =>
        p => files.Contains(p, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Resolve_TakesTheFirstPathHit_NotOurs()
    {
        // The real shape of the risk: our dir is appended last, so an earlier dir wins.
        var resolved = PathResolver.Resolve(
            @"C:\Users\me\.local\bin;C:\token-stack\rtk", Ext, "rtk",
            Present(@"C:\Users\me\.local\bin\rtk.EXE", @"C:\token-stack\rtk\rtk.EXE"));
        Assert.Equal(@"C:\Users\me\.local\bin\rtk.EXE", resolved);
    }

    [Fact]
    public void Resolve_HonoursPathExtOrder_SoACmdShimCanShadowAnExe()
    {
        // .COM precedes .EXE in PATHEXT, so a planted rtk.com beats a real rtk.exe in the SAME dir.
        var resolved = PathResolver.Resolve(@"C:\d", Ext, "rtk",
            Present(@"C:\d\rtk.COM", @"C:\d\rtk.EXE"));
        Assert.Equal(@"C:\d\rtk.COM", resolved);

        // .CMD still resolves when no binary form exists — a batch shim is a valid hijack.
        Assert.Equal(@"C:\d\rtk.CMD",
            PathResolver.Resolve(@"C:\d", Ext, "rtk", Present(@"C:\d\rtk.CMD")));
    }

    [Fact]
    public void Resolve_NullWhenNothingMatches_AndSkipsMalformedEntries()
    {
        Assert.Null(PathResolver.Resolve(@"C:\a;C:\b", Ext, "rtk", Present()));
        Assert.Null(PathResolver.Resolve(null, Ext, "rtk", Present()));
        Assert.Null(PathResolver.Resolve(@"C:\a", Ext, "", Present()));

        // a junk entry must not stop the walk before the real hit
        Assert.Equal(@"C:\good\rtk.EXE", PathResolver.Resolve(
            "\"C:\\quo\u0000ted\";;   ;C:\\good", Ext, "rtk", Present(@"C:\good\rtk.EXE")));
    }

    [Fact]
    public void Resolve_FallsBackToADefaultPathExt_WhenUnset()
    {
        Assert.Equal(@"C:\d\rtk.EXE",
            PathResolver.Resolve(@"C:\d", null, "rtk", Present(@"C:\d\rtk.EXE")));
    }

    [Fact]
    public void Resolve_StripsQuotesAroundPathEntries()
    {
        Assert.Equal(@"C:\d\rtk.EXE",
            PathResolver.Resolve("\"C:\\d\"", Ext, "rtk", Present(@"C:\d\rtk.EXE")));
    }

    [Fact]
    public void SamePath_IgnoresCaseTrailingSlashAndRelativeSegments()
    {
        Assert.True(PathResolver.SamePath(@"C:\TS\rtk\rtk.exe", @"c:\ts\rtk\rtk.exe"));
        Assert.True(PathResolver.SamePath(@"C:\ts\rtk\", @"C:\ts\rtk"));
        Assert.True(PathResolver.SamePath(@"C:\ts\x\..\rtk\rtk.exe", @"C:\ts\rtk\rtk.exe"));
        Assert.False(PathResolver.SamePath(@"C:\other\rtk.exe", @"C:\ts\rtk\rtk.exe"));
        Assert.False(PathResolver.SamePath(null, @"C:\ts\rtk\rtk.exe"));
    }
}
