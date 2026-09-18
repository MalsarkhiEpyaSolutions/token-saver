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
        else if (!DriveExists(c.InstallRoot))
            errors.Add($"installRoot drive {Path.GetPathRoot(c.InstallRoot)} does not exist on this machine");
        if (c.Headroom.Port is < 1024 or > 65535)
            errors.Add("headroom.port must be 1024..65535");
        if (!Modes.Contains(c.Headroom.Mode))
            errors.Add("headroom.mode must be one of: token, cache, passthrough");
        if (c.Rtk.HookMatcher != "Bash")
            errors.Add("rtk.hookMatcher must be \"Bash\" (RTK cannot wrap PowerShell aliases; see spec §5.4)");
        return errors;
    }

    /// <summary>Catches the `--root Z:\token-stack` typo before the install spends minutes
    /// building a venv into a drive that is not there. UNC roots have no drive letter to
    /// probe, so they are accepted and left to fail loudly at the first write instead.</summary>
    private static bool DriveExists(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        return root.StartsWith(@"\\", StringComparison.Ordinal) || Directory.Exists(root);
    }
}
