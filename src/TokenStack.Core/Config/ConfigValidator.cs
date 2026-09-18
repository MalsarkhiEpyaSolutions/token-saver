namespace TokenStack.Core.Config;

public static class ConfigValidator
{
    private static readonly string[] Modes = { "token", "cache", "passthrough" };

    public static List<string> Validate(StackConfig c)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(c.InstallRoot) || !Path.IsPathRooted(c.InstallRoot))
            errors.Add("installRoot must be an absolute path");
        else if (c.InstallRoot.Contains(' '))
            errors.Add("installRoot must not contain spaces (breaks hook quoting; see spec §5.0)");
        else if (DriveProblem(c.InstallRoot) is { } problem)
            errors.Add($"installRoot {problem}");
        if (c.Headroom.Port is < 1024 or > 65535)
            errors.Add("headroom.port must be 1024..65535");
        if (!Modes.Contains(c.Headroom.Mode))
            errors.Add("headroom.mode must be one of: token, cache, passthrough");
        if (c.Rtk.HookMatcher != "Bash")
            errors.Add("rtk.hookMatcher must be \"Bash\" (RTK cannot wrap PowerShell aliases; see spec §5.4)");
        return errors;
    }

    /// <summary>Why this root cannot hold the stack, or null if it can. Catches the
    /// `--root Z:\...` typo before the install spends minutes building a venv into a drive that
    /// is not there — and, more importantly, refuses removable and network locations. The proxy
    /// runs from a Scheduled Task that fires at logon, before a USB stick or a mapped share is
    /// reliably available, and their drive letters move; the result is a stack that installs
    /// perfectly and is silently dead on the next boot, which is far harder to diagnose than
    /// being told "no" here.</summary>
    private static string? DriveProblem(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return "has no drive root";
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return $"is a network path ({root}) — the proxy's Scheduled Task starts at logon, "
                 + "before network paths are reachable. Use a fixed drive.";
        if (!Directory.Exists(root))
            return $"drive {root} does not exist on this machine";

        try
        {
            return new DriveInfo(root).DriveType switch
            {
                DriveType.Removable => $"is on removable drive {root} — the Scheduled Task starts "
                    + "at logon, before a removable drive is reliably attached, and its letter "
                    + "can change. Use a fixed drive.",
                DriveType.Network => $"is on network drive {root} — the Scheduled Task starts at "
                    + "logon, before network drives are mapped. Use a fixed drive.",
                DriveType.CDRom => $"is on read-only drive {root}",
                _ => null,
            };
        }
        catch
        {
            return null; // drive info we cannot read is not itself a reason to refuse
        }
    }
}
