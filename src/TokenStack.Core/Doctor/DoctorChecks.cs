using System.Text.Json.Nodes;
using TokenStack.Core.Claude;
using TokenStack.Core.Components;
using TokenStack.Core.Config;
using TokenStack.Core.Windows;

namespace TokenStack.Core.Doctor;

public sealed record DoctorContext(
    StackConfig Config,
    JsonNode Settings,      // loaded %USERPROFILE%\.claude\settings.json
    JsonNode ClaudeJson,    // loaded %USERPROFILE%\.claude.json
    IEnvStore Env,
    IPortProbe Port,
    IProcessRunner Runner,
    IHttpProbe Http)
{
    public bool SettingsChanged { get; set; }
    public bool ClaudeJsonChanged { get; set; }
}

public sealed record CheckResult(string Id, bool Ok, string Detail, bool CanFix);

public interface IDoctorCheck
{
    string Id { get; }
    CheckResult Detect(DoctorContext ctx);
    /// <summary>Apply the remediation. Returns true when something was changed.</summary>
    bool Fix(DoctorContext ctx);
}

public static class DoctorRegistry
{
    public static IReadOnlyList<IDoctorCheck> All { get; } = new IDoctorCheck[]
    {
        new RoutingBypassedCheck(), new ProxyZombieCheck(), new ProxyExtraMissingCheck(),
        new SembleUvxCheck(), new RtkHookMissingCheck(), new RtkHookPowershellCheck(),
        new RtkHookDuplicateCheck(), new RtkHookUnresolvableCheck(), new RipgrepMissingCheck(),
        new CcoHookMissingCheck(),
        new ModelPinLeftoverCheck(), new PathSpacesCheck(), new TaskMisconfiguredCheck(),
        new DisabledDriftCheck(), new OfflineModelsPresentCheck(),
    };
}

public sealed class RoutingBypassedCheck : IDoctorCheck
{
    public string Id => "routing-bypassed";
    public CheckResult Detect(DoctorContext ctx)
    {
        if (!ctx.Config.Routing.Desktop && !ctx.Config.Routing.Cli)
            return new(Id, true, "routing disabled in config", false);
        var d = new RoutingManager(ctx.Env).Diagnose(ctx.Config.Headroom.Port);
        if (d.SessionRouted) return new(Id, true, "session is ROUTED", false);
        var why = d.Conflict
            ? "User-scope var is correct but THIS session inherited a different value — fully quit Claude (tray) and relaunch"
            : "ANTHROPIC_BASE_URL does not point at the proxy";
        return new(Id, false, why, true);
    }
    public bool Fix(DoctorContext ctx)
    {
        var rm = new RoutingManager(ctx.Env);
        if (ctx.Config.Routing.Desktop) rm.ApplyDesktop(ctx.Config.Headroom.Port);
        if (ctx.Config.Routing.Cli)
            ctx.SettingsChanged |= ClaudeSurgeon.SetEnvBaseUrl(
                ctx.Settings, RoutingManager.ProxyUrl(ctx.Config.Headroom.Port));
        return true;
    }
}

public sealed class ProxyZombieCheck : IDoctorCheck
{
    public string Id => "proxy-zombie";
    public CheckResult Detect(DoctorContext ctx)
    {
        if (!ctx.Config.Headroom.Enabled) return new(Id, true, "headroom disabled", false);
        var tasks = new ScheduledTaskManager(ctx.Runner);
        var zombie = tasks.IsRunning() && !ctx.Port.IsListening(ctx.Config.Headroom.Port);
        return zombie
            ? new(Id, false, "task Running but port dead (zombie listener)", true)
            : new(Id, true, "proxy lifecycle healthy", false);
    }
    public bool Fix(DoctorContext ctx)
    {
        new ScheduledTaskManager(ctx.Runner).RestartWithZombieKill();
        return true;
    }
}

public sealed class ProxyExtraMissingCheck : IDoctorCheck
{
    public string Id => "proxy-extra-missing";
    public CheckResult Detect(DoctorContext ctx)
    {
        if (!ctx.Config.Headroom.Enabled) return new(Id, true, "headroom disabled", false);
        var sitePackages = Path.Combine(ctx.Config.InstallRoot, "venv", "Lib", "site-packages");
        if (!Directory.Exists(sitePackages)) return new(Id, true, "venv not installed yet", false);
        var ok = Directory.Exists(Path.Combine(sitePackages, "fastapi"));
        return ok
            ? new(Id, true, "[proxy] extra present (fastapi found)", false)
            : new(Id, false, "venv lacks fastapi — installed without the [proxy] extra", true);
    }
    public bool Fix(DoctorContext ctx)
    {
        var uv = new Bootstrap(ctx.Runner).EnsureUv();
        var py = Path.Combine(ctx.Config.InstallRoot, "venv", "Scripts", "python.exe");
        return ctx.Runner.Run(uv,
            $"pip install --python {py} headroom-ai[proxy]=={ctx.Config.Headroom.Version}", 600000).Ok;
    }
}

public sealed class SembleUvxCheck : IDoctorCheck
{
    public string Id => "semble-uvx";
    public CheckResult Detect(DoctorContext ctx)
    {
        if (!ctx.Config.Semble.Enabled) return new(Id, true, "semble disabled", false);
        var cmd = ctx.ClaudeJson["mcpServers"]?["semble"]?["command"]?.GetValue<string>();
        if (cmd is null) return new(Id, false, "semble MCP not registered", true);
        if (cmd.Contains("uvx", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(cmd))
            return new(Id, false, "semble MCP uses uvx/relative command — handshake will silently fail", true);
        if (!File.Exists(cmd)) return new(Id, false, $"semble exe missing at {cmd}", true);
        return new(Id, true, "semble MCP wired to installed exe", false);
    }
    public bool Fix(DoctorContext ctx)
    {
        ctx.ClaudeJsonChanged |= ClaudeSurgeon.EnsureSembleMcp(ctx.ClaudeJson, SembleComponent.ExePath());
        return true;
    }
}

/// <summary>`rtk verify` reports only the bare `rtk hook claude` spelling, so people add that by
/// hand next to the full-path entry the installer writes, believing ours is broken. It is not —
/// both spellings were measured to return byte-identical rewrites, and feeding an
/// already-rewritten command back through the hook produces no output, so nothing is corrupted.
/// The cost is real but narrow: rtk is spawned once per duplicate on every single Bash call.</summary>
public sealed class RtkHookDuplicateCheck : IDoctorCheck
{
    public string Id => "rtk-hook-duplicate";

    public CheckResult Detect(DoctorContext ctx)
    {
        if (!ctx.Config.Rtk.Enabled) return new(Id, true, "rtk disabled", false);
        var n = ClaudeSurgeon.CountRtkHooks(ctx.Settings);
        return n <= 1
            ? new(Id, true, "one rtk hook", false)
            : new(Id, false, $"{n} rtk hooks wired — rtk runs {n}x per Bash call", true);
    }

    /// <summary>Collapses to the bare spelling only when a bare `rtk` provably resolves to OUR
    /// exe. Otherwise the full path is the only form that is both live and unsubstitutable.</summary>
    public bool Fix(DoctorContext ctx) =>
        RtkHookForm.Rewrite(ctx, bare: RtkComponent.BareHookIsSafe(ctx.Env, ctx.Config));
}

/// <summary>rtk shells out to ripgrep for `rtk grep`. Without it rtk falls back to a direct exec
/// and prints "Failed to resolve 'rg' via PATH" on every search — a warning that is easy to read
/// as a broken install when it is really a missing optional accelerator. Not auto-fixable: we do
/// not ship rg, and installing a package manager's package behind the user's back is worse than
/// telling them the one command to run.</summary>
public sealed class RipgrepMissingCheck : IDoctorCheck
{
    public string Id => "ripgrep-missing";

    public CheckResult Detect(DoctorContext ctx)
    {
        if (!ctx.Config.Rtk.Enabled) return new(Id, true, "rtk disabled", false);
        return ctx.Runner.Run("rg", "--version", 15000).Ok
            ? new(Id, true, "ripgrep on PATH", false)
            : new(Id, false, "ripgrep (rg) not on PATH — `rtk grep` degrades and warns on every "
                           + "search. Install: winget install BurntSushi.ripgrep.MSVC", false);
    }

    public bool Fix(DoctorContext ctx) => false;
}

public sealed class RtkHookMissingCheck : IDoctorCheck
{
    public string Id => "rtk-hook-missing";
    public CheckResult Detect(DoctorContext ctx)
    {
        if (!ctx.Config.Rtk.Enabled) return new(Id, true, "rtk disabled", false);
        // "Absent" must mean no rtk hook in ANY spelling. Matching only "rtk.exe" reported a
        // hand-added bare `rtk hook claude` as missing while it was filtering perfectly.
        if (ClaudeSurgeon.CountRtkHooks(ctx.Settings) == 0)
            return new(Id, false, "PreToolUse rtk hook absent", true);

        var exePath = RtkHookForm.FullPathCommand(ctx.Settings);
        if (exePath is null)
            return new(Id, true, "rtk hook wired (bare `rtk hook claude`, resolved via PATH)", false);
        var exe = Path.Combine(ctx.Config.InstallRoot, "rtk", "rtk.exe");
        return exePath.Contains(exe, StringComparison.OrdinalIgnoreCase)
            ? new(Id, true, "rtk hook wired (full-path fallback)", false)
            : new(Id, false, $"rtk hook points at a stale path: {exePath}", true);
    }
    public bool Fix(DoctorContext ctx) =>
        RtkHookForm.Rewrite(ctx, bare: RtkComponent.BareHookIsSafe(ctx.Env, ctx.Config));
}

/// <summary>The bare `rtk hook claude` spelling is the one rtk's own self-check recognizes, so it
/// is what install writes — but a bare command is only as trustworthy as the first PATH hit, and
/// it has two distinct failure modes, both of which this check owns:
///
/// 1. <b>Nothing resolves</b> — Claude cannot run the hook command and filtering stops
///    **silently**. Nothing is printed anywhere; the stack just quietly stops saving tokens.
/// 2. <b>Something else resolves</b> — a different rtk earlier on PATH receives every Bash
///    command on stdin and returns the `updatedInput.command` Claude then executes. Our dir is
///    appended to the USER PATH, which Windows searches after all of MACHINE PATH, so many
///    directories outrank it — including several writable without admin. The user's own notes
///    record a benign instance of this (a different project also ships an `rtk`).
///
/// Asking "does `rtk --version` succeed?" cannot tell these apart from a healthy install: a
/// planted binary answers it happily. Only comparing the resolved path to the exe we installed
/// can. Both modes remediate the same way — pin the absolute path, trading rtk's cosmetic
/// warning line for a command that cannot be substituted.</summary>
public sealed class RtkHookUnresolvableCheck : IDoctorCheck
{
    public string Id => "rtk-hook-unresolvable";

    public CheckResult Detect(DoctorContext ctx)
    {
        if (!ctx.Config.Rtk.Enabled) return new(Id, true, "rtk disabled", false);
        if (ClaudeSurgeon.CountRtkHooks(ctx.Settings) == 0)
            return new(Id, true, "no rtk hook to resolve", false);   // rtk-hook-missing owns this
        if (RtkHookForm.FullPathCommand(ctx.Settings) is not null)
            return new(Id, true, "hook pins the full path — no PATH lookup", false);

        var resolved = RtkComponent.ResolveOnPath(ctx.Env);
        if (resolved is null)
            return new(Id, false, "hook is the bare `rtk hook claude` but NO rtk resolves on PATH "
                                + "— filtering is silently off. Fix pins the full path.", true);
        var ours = Path.Combine(ctx.Config.InstallRoot, "rtk", "rtk.exe");
        if (!PathResolver.SamePath(resolved, ours))
            return new(Id, false, $"a DIFFERENT rtk shadows ours on PATH ({resolved}) — it is handed "
                                + "every Bash command and chooses the rewrite. Fix pins the full path.", true);
        return new(Id, true, "bare hook resolves to our rtk", false);
    }

    public bool Fix(DoctorContext ctx) => RtkHookForm.Rewrite(ctx, bare: false);
}

internal static class RtkHookForm
{
    /// <summary>The wired rtk hook command when it names an .exe, else null (= the bare form).</summary>
    public static string? FullPathCommand(JsonNode settings) =>
        settings["hooks"]?["PreToolUse"]?.AsArray()
            .Select(e => e?["hooks"]?[0]?["command"]?.GetValue<string>())
            .FirstOrDefault(c => c?.Contains("rtk.exe", StringComparison.OrdinalIgnoreCase) == true);

    public static bool Rewrite(DoctorContext ctx, bool bare)
    {
        ctx.SettingsChanged |= ClaudeSurgeon.EnsureRtkHook(ctx.Settings,
            Path.Combine(ctx.Config.InstallRoot, "rtk", "rtk.exe"),
            ctx.Config.Rtk.HookMatcher, bare);
        return true;
    }
}

public sealed class RtkHookPowershellCheck : IDoctorCheck
{
    public string Id => "rtk-hook-powershell";
    public CheckResult Detect(DoctorContext ctx)
    {
        var pre = ctx.Settings["hooks"]?["PreToolUse"]?.AsArray();
        var bad = pre?.Any(e =>
            e?["matcher"]?.GetValue<string>()?.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) == true
            && e["hooks"]?[0]?["command"]?.GetValue<string>()
                ?.Contains("rtk", StringComparison.OrdinalIgnoreCase) == true) == true;
        return bad
            ? new(Id, false, "rtk hook has a PowerShell matcher — rtk cannot wrap PS aliases", true)
            : new(Id, true, "no PowerShell rtk matcher", false);
    }
    public bool Fix(DoctorContext ctx)
    {
        var pre = ctx.Settings["hooks"]?["PreToolUse"]?.AsArray();
        if (pre is null) return false;
        var bad = pre.Where(e =>
            e?["matcher"]?.GetValue<string>()?.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) == true
            && e["hooks"]?[0]?["command"]?.GetValue<string>()
                ?.Contains("rtk", StringComparison.OrdinalIgnoreCase) == true).ToList();
        foreach (var b in bad) pre.Remove(b);
        ctx.SettingsChanged |= bad.Count > 0;
        return bad.Count > 0;
    }
}

public sealed class CcoHookMissingCheck : IDoctorCheck
{
    public string Id => "cco-hook-missing";
    public CheckResult Detect(DoctorContext ctx)
    {
        if (!ctx.Config.Cco.Enabled) return new(Id, true, "cco disabled", false);
        var pre = ctx.Settings["hooks"]?["PreToolUse"]?.AsArray();
        var wired = pre?.Any(e => e?["hooks"]?[0]?["command"]?.GetValue<string>()
            ?.Contains("read-cache.js", StringComparison.OrdinalIgnoreCase) == true) == true;
        return wired
            ? new(Id, true, "cco read-cache hook present", false)
            : new(Id, false, "cco enabled but read-cache hook missing", true);
    }
    public bool Fix(DoctorContext ctx)
    {
        var changed = ClaudeSurgeon.EnsureCcoHooks(ctx.Settings, CcoComponent.ReadCacheJs(ctx.Config));
        ctx.SettingsChanged |= changed;
        return changed;
    }
}

public sealed class ModelPinLeftoverCheck : IDoctorCheck
{
    public string Id => "model-pin-leftover";
    public CheckResult Detect(DoctorContext ctx) =>
        ctx.Env.GetUser("ANTHROPIC_MODEL") is { } v
            ? new(Id, false, $"stray User-scope ANTHROPIC_MODEL={v} pins every session's model", true)
            : new(Id, true, "no model pin", false);
    public bool Fix(DoctorContext ctx) { ctx.Env.SetUser("ANTHROPIC_MODEL", null); return true; }
}

public sealed class PathSpacesCheck : IDoctorCheck
{
    public string Id => "path-spaces";
    public CheckResult Detect(DoctorContext ctx) =>
        ctx.Config.InstallRoot.Contains(' ')
            ? new(Id, false, "installRoot contains spaces — reinstall to a space-free root " +
                  "(token-stack uninstall, edit config, token-stack install)", false)
            : new(Id, true, "installRoot is space-free", false);
    public bool Fix(DoctorContext ctx) => false; // guided, never automatic
}

public sealed class TaskMisconfiguredCheck : IDoctorCheck
{
    public string Id => "task-misconfigured";
    public CheckResult Detect(DoctorContext ctx)
    {
        if (!ctx.Config.Headroom.Enabled) return new(Id, true, "headroom disabled", false);
        var r = ctx.Runner.Run("schtasks", $"/query /tn {ScheduledTaskManager.DefaultTaskName} /xml", 15000);
        if (!r.Ok) return new(Id, false, "HeadroomProxy task not registered", true);
        var expectedCmd = Path.Combine(ctx.Config.InstallRoot, "venv", "Scripts", "pythonw.exe");
        return r.StdOut.Contains(expectedCmd, StringComparison.OrdinalIgnoreCase)
            ? new(Id, true, "task action matches managed layout", false)
            : new(Id, false, "task action drifted from managed layout", true);
    }
    public bool Fix(DoctorContext ctx)
    {
        var xml = TaskXml.Render(
            Path.Combine(ctx.Config.InstallRoot, "venv", "Scripts", "pythonw.exe"),
            Path.Combine(ctx.Config.InstallRoot, "run_proxy.py"),
            ctx.Config.InstallRoot,
            Environment.UserDomainName + "\\" + Environment.UserName);
        new ScheduledTaskManager(ctx.Runner)
            .RegisterOrUpdate(xml, Path.Combine(ctx.Config.InstallRoot, "tmp"));
        return true;
    }
}

/// <summary>Offline installs seed HuggingFace models into installRoot\hf-cache. If that dir
/// exists (= an offline install) it must actually contain model dirs, else the proxy will try
/// to reach HuggingFace at runtime and fail on an air-gapped machine. Online installs have no
/// hf-cache dir and pass trivially.</summary>
public sealed class OfflineModelsPresentCheck : IDoctorCheck
{
    public string Id => "offline-models-present";
    public CheckResult Detect(DoctorContext ctx)
    {
        var hf = Path.Combine(ctx.Config.InstallRoot, "hf-cache");
        if (!Directory.Exists(hf))
            return new(Id, true, "online install (no bundled model cache)", false);
        var hub = Path.Combine(hf, "hub");
        var hasModels = Directory.Exists(hub)
            && Directory.EnumerateDirectories(hub, "models--*").Any();
        return hasModels
            ? new(Id, true, "bundled HuggingFace models present", false)
            : new(Id, false, $"hf-cache exists but has no models at {hub} — re-pack/re-install offline", false);
    }
    public bool Fix(DoctorContext ctx) => false; // guided: rebuild the offline bundle
}

public sealed class DisabledDriftCheck : IDoctorCheck
{
    public string Id => "stack-disabled-drift";
    public CheckResult Detect(DoctorContext ctx)
    {
        var drift = new List<string>();
        // Counts every spelling: matching "rtk.exe" alone missed a wired bare hook entirely.
        if (!ctx.Config.Rtk.Enabled && ClaudeSurgeon.CountRtkHooks(ctx.Settings) > 0)
            drift.Add("rtk disabled but hook wired");
        var sembleWired = ctx.ClaudeJson["mcpServers"]?["semble"] is not null;
        if (!ctx.Config.Semble.Enabled && sembleWired) drift.Add("semble disabled but MCP wired");
        return drift.Count == 0
            ? new(Id, true, "wiring matches config", false)
            : new(Id, false, string.Join("; ", drift), true);
    }
    public bool Fix(DoctorContext ctx)
    {
        var changed = false;
        if (!ctx.Config.Rtk.Enabled)
        {
            var c1 = ClaudeSurgeon.RemoveRtkHook(ctx.Settings);
            ctx.SettingsChanged |= c1; changed |= c1;
        }
        if (!ctx.Config.Semble.Enabled)
        {
            var c2 = ClaudeSurgeon.RemoveSembleMcp(ctx.ClaudeJson);
            ctx.ClaudeJsonChanged |= c2; changed |= c2;
        }
        return changed;
    }
}
