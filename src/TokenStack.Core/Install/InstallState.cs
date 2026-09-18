using TokenStack.Core.Config;

namespace TokenStack.Core.Install;

public enum InstallVerdict
{
    /// <summary>No config.json anywhere we look — a first install.</summary>
    NotInstalled,

    /// <summary>Installed version is the same as (or newer than) this build. Nothing to do;
    /// re-running is a repair, not an upgrade.</summary>
    UpToDate,

    /// <summary>Installed version is older than this build.</summary>
    UpdateAvailable,

    /// <summary>A stack is installed but did not record its version — every install before the
    /// `version` field existed. Treated as upgradable, because assuming "current" would strand
    /// those machines on an old build forever.</summary>
    UnknownVersion,
}

public sealed record InstallSnapshot(
    InstallVerdict Verdict,
    string? Root,
    string? InstalledVersion,
    string SetupVersion)
{
    public bool AlreadyCurrent => Verdict is InstallVerdict.UpToDate;
}

/// <summary>Answers "is anything installed, and is it current?" BEFORE the installer spends
/// minutes rebuilding a stack that is already correct.</summary>
public static class InstallState
{
    /// <summary>Pure comparison — all the decisions, none of the I/O.</summary>
    public static InstallSnapshot Compare(string? root, string? installedVersion, string setupVersion)
    {
        if (string.IsNullOrWhiteSpace(root))
            return new(InstallVerdict.NotInstalled, null, null, setupVersion);

        if (string.IsNullOrWhiteSpace(installedVersion)
            || !Version.TryParse(Trim(installedVersion), out var installed))
            return new(InstallVerdict.UnknownVersion, root, installedVersion, setupVersion);

        // An unparsable SETUP version is our own bug, not the user's — never claim their
        // install is stale on the strength of a version we cannot read.
        if (!Version.TryParse(Trim(setupVersion), out var setup))
            return new(InstallVerdict.UnknownVersion, root, installedVersion, setupVersion);

        // installed > setup (an older setup run over a newer install) counts as current: there
        // is nothing to update, and we must never silently downgrade a working stack.
        return new(
            installed < setup ? InstallVerdict.UpdateAvailable : InstallVerdict.UpToDate,
            root, installedVersion, setupVersion);
    }

    /// <summary>Strips build metadata such as "1.4.0+abc123" that InformationalVersion carries.</summary>
    private static string Trim(string v) => v.Split('+', ' ')[0];

    /// <summary>Reads the recorded install, if any. A missing or corrupt config.json means
    /// "not installed" rather than an error — the caller's next move is to install anyway.</summary>
    public static InstallSnapshot Inspect(string setupVersion, string? configPath = null)
    {
        var path = configPath ?? ConfigStore.DefaultPath;
        try
        {
            if (!File.Exists(path)) return Compare(null, null, setupVersion);
            var cfg = ConfigStore.Load(path);
            return Compare(cfg.InstallRoot, cfg.Version, setupVersion);
        }
        catch
        {
            return Compare(null, null, setupVersion);
        }
    }
}
