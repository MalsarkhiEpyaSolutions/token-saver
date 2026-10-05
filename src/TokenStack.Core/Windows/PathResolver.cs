namespace TokenStack.Core.Windows;

/// <summary>Resolves a bare command name to the file Windows would actually execute, by walking
/// PATH in order and applying PATHEXT. Pure (filesystem probe is injectable) so the hook-safety
/// decision is unit-testable.
///
/// This exists because a bare hook command is only as trustworthy as the first PATH hit. Our
/// installer appends rtk's dir to the USER PATH, and Windows searches the whole MACHINE PATH
/// first, so dozens of directories — several of them writable without admin — outrank ours. A
/// planted rtk.exe (or rtk.cmd, via PATHEXT) would be handed every Bash command and chooses the
/// rewrite Claude then runs, so "does `rtk --version` work?" is NOT a safety check: a hostile
/// binary answers that happily. Only comparing the resolved path to the exe we installed is.</summary>
public static class PathResolver
{
    /// <summary>What cmd.exe falls back to when PATHEXT is unset.</summary>
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /// <summary>First file on <paramref name="pathVar"/> that Windows would run for
    /// <paramref name="command"/>, or null when nothing matches.</summary>
    public static string? Resolve(string? pathVar, string? pathExt, string command,
                                  Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        exists ??= File.Exists;

        var exts = Split(string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt);
        // An explicit extension is tried as given before any PATHEXT is appended.
        var spellings = Path.HasExtension(command)
            ? new[] { command }.Concat(exts.Select(e => command + e))
            : exts.Select(e => command + e);
        var names = spellings.ToArray();

        foreach (var dir in Split(pathVar))
        {
            // A single malformed PATH entry must never take doctor down with it.
            foreach (var name in names)
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim('"'), name); }
                catch (ArgumentException) { break; }
                if (exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null) return false;
        try
        {
            return Path.GetFullPath(a).TrimEnd('\\')
                .Equals(Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static string[] Split(string? v) => (v ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
