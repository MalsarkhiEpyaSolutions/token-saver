namespace TokenStack.Core.Components;

/// <summary>The optional "Concise Plus" output style: Claude Code's built-in Concise rules plus
/// one addition — suggestion lists capped at five, never capping exhaustive results. Unlike the
/// four real layers this is a pure preference, so it is opt-in: `install` asks once and the
/// answer sticks in config.json. It is a single .md dropped into ~\.claude\output-styles plus a
/// settings.json key; nothing runs, nothing is downloaded.</summary>
public static class OutputStyleComponent
{
    public const string ResourceName = "TokenStack.Core.concise-plus.md";
    public const string StyleName = "Concise Plus";
    public const string FileName = "concise-plus.md";

    public static string StyleDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "output-styles");

    public static string StylePath => Path.Combine(StyleDir, FileName);

    public static string ReadEmbedded()
    {
        using var stream = typeof(OutputStyleComponent).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{ResourceName}' not found");
        return new StreamReader(stream).ReadToEnd();
    }

    public static void Install()
    {
        Directory.CreateDirectory(StyleDir);
        File.WriteAllText(StylePath, ReadEmbedded());
    }

    /// <summary>Best effort: a locked or already-absent file is not worth failing an install over.</summary>
    public static void Remove()
    {
        try { File.Delete(StylePath); } catch { /* absent or locked */ }
    }

    /// <summary>Decide whether the style is on. `current` is config.json's tri-state: null means
    /// we have never asked, so ask if we can. A non-interactive install (`irm | iex` piped, CI,
    /// `--component` repair) passes ask=null and must never block — it defaults to OFF. Once the
    /// user has answered either way, the answer is kept and they are never asked again.</summary>
    public static bool Resolve(bool? current, Func<bool>? ask) => current ?? ask?.Invoke() ?? false;
}
