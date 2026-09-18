using System.Text;

namespace TokenStack.Core.Windows;

/// <summary>WindowStyle 7 = minimized, right for a toggle that just flips a flag and notifies.
/// A shortcut whose command ASKS something must use 1 (normal) or the prompt is hidden.</summary>
public sealed record ShortcutSpec(
    string FileName,
    string Arguments,
    int WindowStyle = 7,
    string Description = "token-stack toggle");

/// <summary>Creates/removes the desktop toggle buttons via WScript.Shell COM (through
/// PowerShell — no extra dependency, works on every Windows edition):
///   • a loose "Token Stack" shortcut = the whole-stack quick toggle,
///   • a "Token Stack Controls" folder holding one per-layer toggle (Headroom/RTK/Semble/CCO).</summary>
public sealed class ShortcutCreator(IProcessRunner runner)
{
    private static string Desktop =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string WholeStackPath => Path.Combine(Desktop, "Token Stack.lnk");
    public static string ControlsFolder => Path.Combine(Desktop, "Token Stack Controls");

    /// <summary>The per-layer toggles that live inside the controls folder.</summary>
    public static IReadOnlyList<ShortcutSpec> LayerSpecs() => new[]
    {
        new ShortcutSpec("Headroom (toggle).lnk", "toggle headroom --notify"),
        new ShortcutSpec("RTK (toggle).lnk", "toggle rtk --notify"),
        new ShortcutSpec("Semble (toggle).lnk", "toggle semble --notify"),
        new ShortcutSpec("CCO read-cache (toggle).lnk", "toggle cco --notify"),
    };

    /// <summary>The uninstall button, in the same folder as the toggles.
    /// Two deliberate differences from every other shortcut:
    ///   • WindowStyle 1, not 7 — `uninstall` asks before it changes anything, and a minimized
    ///     window would hide that question behind a taskbar blink: the user clicks, sees
    ///     nothing happen, and the process sits waiting for an answer they cannot see.
    ///   • plain `uninstall`, NOT `--purge` and NOT `-y` — a desktop icon is one mis-click from
    ///     being run, so it unwires (reversible by re-installing) and leaves the files. The
    ///     command's own closing note tells the user how to delete them for good.</summary>
    public static ShortcutSpec UninstallSpec() => new(
        "Uninstall TokenSaver.lnk", "uninstall",
        WindowStyle: 1,
        Description: "Unwire the token stack (asks first; files are kept - use `uninstall --purge` to delete them)");

    /// <summary>Create the loose whole-stack button + the per-layer folder. Returns the folder.</summary>
    public string CreateAll(string exePath)
    {
        Directory.CreateDirectory(ControlsFolder);

        var ps = new StringBuilder("$w=New-Object -ComObject WScript.Shell;");
        Append(ps, WholeStackPath, exePath, new ShortcutSpec("", "toggle --notify"));
        foreach (var s in LayerSpecs().Append(UninstallSpec()))
            Append(ps, Path.Combine(ControlsFolder, s.FileName), exePath, s);

        var r = runner.Run("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"", 30000);
        if (!r.Ok)
            throw new InvalidOperationException($"could not create desktop shortcuts: {r.StdErr}{r.StdOut}");
        return ControlsFolder;
    }

    /// <summary>One on/off toggle button for a model profile, in the controls folder.</summary>
    public string CreateProfileToggle(string exePath, string profileName, string slug)
    {
        Directory.CreateDirectory(ControlsFolder);
        var lnk = Path.Combine(ControlsFolder, $"{profileName} (toggle).lnk");
        var ps = new StringBuilder("$w=New-Object -ComObject WScript.Shell;");
        Append(ps, lnk, exePath, new ShortcutSpec("", $"profile toggle {slug}"));
        var r = runner.Run("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"", 30000);
        if (!r.Ok)
            throw new InvalidOperationException($"could not create profile toggle: {r.StdErr}{r.StdOut}");
        return lnk;
    }

    private static void Append(StringBuilder ps, string lnk, string exe, ShortcutSpec spec) =>
        ps.Append($"$s=$w.CreateShortcut('{lnk}');$s.TargetPath='{exe}';$s.Arguments='{spec.Arguments}';")
          .Append($"$s.IconLocation='{exe},0';$s.WindowStyle={spec.WindowStyle};")
          .Append($"$s.Description='{spec.Description.Replace("'", "''")}';$s.Save();");

    public void Remove()
    {
        try { if (File.Exists(WholeStackPath)) File.Delete(WholeStackPath); } catch { /* best effort */ }
        try { if (Directory.Exists(ControlsFolder)) Directory.Delete(ControlsFolder, recursive: true); }
        catch { /* best effort */ }
    }
}
