using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenStack.Core.Config;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Install root used when this machine has never installed. Deliberately OUTSIDE
    /// the user profile: when the installer runs inside an MSIX-packaged host (e.g. Claude
    /// Desktop spawning it), every %LOCALAPPDATA% write is silently virtualized into the
    /// package's LocalCache — invisible to Task Scheduler and normal terminals (split-brain,
    /// seen live). C:\token-stack is never virtualized, has no spaces, and needs no admin
    /// (Authenticated Users may create folders under C:\). It is also the pre-1.3 hard-coded
    /// root, so an install that predates the pointer file is still found.</summary>
    public const string LegacyRoot = @"C:\token-stack";

    /// <summary>Records which root THIS machine installed into, so a `--root D:\token-stack`
    /// install is still found by every later command (status, toggle, config, uninstall) —
    /// config.json lives inside the chosen root, so without this we would have no way back to
    /// it. Kept beside ~\.claude because that is already the one location the whole stack
    /// depends on; if the profile were virtualized, Preflight's MSIX guard has already failed
    /// the install. A missing or unreadable pointer silently falls back to LegacyRoot.</summary>
    public static string DefaultPointerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".token-saver-root");

    public static string DefaultRoot => ReadPointer() ?? LegacyRoot;

    public static string DefaultPath => Path.Combine(DefaultRoot, "config.json");

    /// <summary>null when absent, empty, or not an absolute path — every one of those means
    /// "no recorded root", and the caller falls back to LegacyRoot rather than failing.</summary>
    public static string? ReadPointer(string? pointerPath = null)
    {
        try
        {
            var root = File.ReadAllText(pointerPath ?? DefaultPointerPath).Trim();
            return root.Length > 0 && Path.IsPathRooted(root) ? root : null;
        }
        catch { return null; }
    }

    public static void WritePointer(string root, string? pointerPath = null)
        => File.WriteAllText(pointerPath ?? DefaultPointerPath, root);

    public static void DeletePointer(string? pointerPath = null)
    {
        try { File.Delete(pointerPath ?? DefaultPointerPath); } catch { /* absent or locked */ }
    }

    public static StackConfig Load(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))
                   ?? throw new InvalidDataException($"Empty config: {path}");
        return node.Deserialize<StackConfig>(Opts)
               ?? throw new InvalidDataException($"Unreadable config: {path}");
    }

    public static void Save(StackConfig cfg, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, Opts));
    }

    /// <summary>Dot-path read straight from the file (preserving-unknown-keys path).</summary>
    public static string GetValue(string path, string key)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        var target = Walk(node, key) ?? throw new ArgumentException($"Unknown config key: {key}");
        return target.ToJsonString().Trim('"');
    }

    /// <summary>Dot-path write. Validates the key exists in the schema, preserves unknown keys,
    /// and validates the resulting config before saving.</summary>
    public static void SetValue(string path, string key, string value)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var existing = Walk(root, key) ?? throw new ArgumentException($"Unknown config key: {key}");

        JsonNode newNode = existing.GetValueKind() switch
        {
            JsonValueKind.Number => JsonValue.Create(long.Parse(value)),
            JsonValueKind.True or JsonValueKind.False => JsonValue.Create(bool.Parse(value)),
            JsonValueKind.Array => JsonNode.Parse(value)!,
            _ => JsonValue.Create(value)!,
        };

        var parts = key.Split('.');
        var parent = parts.Length == 1 ? root : Walk(root, string.Join('.', parts[..^1]))!;
        parent.AsObject()[parts[^1]] = newNode;

        var candidate = root.Deserialize<StackConfig>(Opts)!;
        var errors = ConfigValidator.Validate(candidate);
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid value for {key}: {string.Join("; ", errors)}");

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonNode? Walk(JsonNode root, string dotPath)
    {
        JsonNode? cur = root;
        foreach (var part in dotPath.Split('.'))
        {
            cur = cur?.AsObject().TryGetPropertyValue(part, out var next) == true ? next : null;
            if (cur is null) return null;
        }
        return cur;
    }
}
