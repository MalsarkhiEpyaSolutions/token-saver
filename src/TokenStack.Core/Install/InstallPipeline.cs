using TokenStack.Core.Claude;
using TokenStack.Core.Components;
using TokenStack.Core.Config;
using TokenStack.Core.Windows;

namespace TokenStack.Core.Install;

public sealed record InstallStep(string Name, Action Run);

public sealed class InstallPipeline(
    IProcessRunner runner, IEnvStore env, IPortProbe port, IHttpProbe http,
    Action<string> log)
{
    public string SettingsPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    public string ClaudeJsonPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public static void Preflight(StackConfig cfg)
    {
        var errors = ConfigValidator.Validate(cfg);
        if (errors.Count > 0)
            throw new InvalidOperationException("Config invalid: " + string.Join("; ", errors)
                + (cfg.InstallRoot.Contains(' ') ? " — choose a root without spaces." : ""));
        Directory.CreateDirectory(cfg.InstallRoot);
        GuardAgainstMsixVirtualization();
    }

    /// <summary>When this process runs inside an MSIX container (e.g. a terminal spawned by
    /// Claude Desktop), profile-dir writes are redirected into the package's LocalCache — the
    /// Scheduled Task and normal terminals then see an EMPTY real path (0x80070002, observed
    /// live). Probes %LOCALAPPDATA%, NOT installRoot: uv and semble always land in
    /// ~\.local\bin and the wiring in ~\.claude, so a non-profile installRoot (the default
    /// C:\token-stack) does not make the install safe — it only hides the symptom until
    /// Claude silently ignores the whole stack.</summary>
    public static void GuardAgainstMsixVirtualization()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var packages = Path.Combine(profile, "AppData", "Local", "Packages");
        if (!Directory.Exists(packages)) return;

        var probeName = $".ts-virt-probe-{Guid.NewGuid():N}";
        var probePath = Path.Combine(profile, "AppData", "Local", probeName);
        File.WriteAllText(probePath, "probe");
        try
        {
            // A virtualized write lands under <pkg>\LocalCache\Local\ (LocalCache mirrors the
            // profile-relative layout). Reading that real path is itself never redirected.
            var virtualized = Directory.EnumerateDirectories(packages)
                .Select(pkg => Path.Combine(pkg, "LocalCache", "Local", probeName))
                .Any(File.Exists);

            if (virtualized)
                throw new InvalidOperationException(
                    "This process is running inside an MSIX package sandbox (e.g. a terminal " +
                    "spawned by Claude Desktop), so every write to your user profile is " +
                    "redirected into that package's LocalCache — uv, semble and " +
                    @"~\.claude\settings.json would all be invisible to Claude and to the " +
                    "Scheduled Task. Re-run `token-saver install` from a REGULAR terminal " +
                    "(Start menu → Windows Terminal or PowerShell).");
        }
        finally
        {
            try { File.Delete(probePath); } catch { /* best effort */ }
        }
    }

    /// <summary>Pure step plan (testable). Run() bodies close over the instance services.</summary>
    public static List<InstallStep> PlanSteps(StackConfig cfg)
    {
        var steps = new List<InstallStep>
        {
            new("preflight", () => { }),
            new("bootstrap-uv", () => { }),
        };
        if (cfg.Headroom.Enabled) steps.Add(new("headroom", () => { }));
        if (cfg.Rtk.Enabled) steps.Add(new("rtk", () => { }));
        if (cfg.Semble.Enabled) steps.Add(new("semble", () => { }));
        if (cfg.Cco.Enabled) steps.Add(new("cco", () => { }));
        steps.Add(new("routing", () => { }));
        steps.Add(new("hooks", () => { }));
        steps.Add(new("save-config", () => { }));
        return steps;
    }

    /// <summary>persistConfig=false for `install --component X` repairs — the in-memory
    /// enabled-flag narrowing must never be written back to config.json. source=null
    /// auto-detects (offline iff a vendor bundle sits next to the running exe).</summary>
    public void Run(StackConfig cfg, bool persistConfig = true, InstallSource? source = null)
    {
        log("[1/8] preflight");
        Preflight(cfg);
        DetectLegacyInstall();

        var src = source ?? InstallSourceResolver.Resolve(
            Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory(), null);

        log(src.IsOffline
            ? $"[2/8] offline bundle detected ({src.VendorDir}) — installing without network"
            : "[2/8] bootstrap: uv");
        var uv = src.IsOffline ? src.Uv : new Bootstrap(runner).EnsureUv();

        if (cfg.Headroom.Enabled)
        {
            DetectUpstream(cfg);
            if (!string.IsNullOrEmpty(cfg.Headroom.UpstreamUrl))
                log($"      adopting upstream: {Providers.Label(cfg.Headroom.UpstreamUrl)} " +
                    $"({cfg.Headroom.UpstreamUrl})");
        }

        var headroom = new HeadroomComponent(runner, port, http);
        if (cfg.Headroom.Enabled)
        {
            log($"[3/8] headroom {cfg.Headroom.Version} (venv + [proxy] + task; cold load 25-105s)"
                + (src.IsOffline ? " + seeding bundled HF models" : ""));
            headroom.Install(cfg, uv, src);
            if (!headroom.WaitReady(cfg.Headroom.Port))
                throw new InvalidOperationException(
                    $"Headroom did not become ready on :{cfg.Headroom.Port} within 120s. " +
                    $"Log: {Path.Combine(cfg.InstallRoot, "proxy.log")}");
            log($"      ready on :{cfg.Headroom.Port}");
        }

        var rtk = new RtkComponent(runner, env);
        if (cfg.Rtk.Enabled)
        {
            log($"[4/8] rtk {cfg.Rtk.Version} ({(src.IsOffline ? "from bundle" : "download")} + PATH)");
            rtk.Install(cfg, src);
        }

        var cco = new CcoComponent(runner);
        if (cfg.Cco.Enabled)
        {
            if (cco.NodePresent())
            {
                log($"[cco] read-cache {cfg.Cco.Version} (extract + disable big-file nudge + node smoke)");
                cco.Install(cfg);
            }
            else
            {
                log("[cco] Node (>=18) not found on PATH — skipping read-cache layer " +
                    "(install Node and run `token-saver on cco` to enable it later).");
                cfg.Cco.Enabled = false;
            }
        }

        var semble = new SembleComponent(runner);
        if (cfg.Semble.Enabled)
        {
            log("[5/8] semble (uv tool install + smoke search)");
            semble.Install(cfg.Semble, uv, src);
            if (!semble.SmokeTest())
                throw new InvalidOperationException(
                    "semble installed but the smoke search failed — run with --verbose for output.");
        }

        log("[6/8] routing");
        if (cfg.Routing.Desktop && cfg.Headroom.Enabled)
        {
            new RoutingManager(env).ApplyDesktop(cfg.Headroom.Port);
            log("      User-scope ANTHROPIC_BASE_URL set (Desktop ignores settings.json env)");
        }

        log("[7/8] claude wiring (settings.json + .claude.json, one backup each)");
        CopySelfToRoot(cfg);
        ApplyClaudeWiring(cfg);
        try
        {
            new ShortcutCreator(runner).CreateAll(Path.Combine(cfg.InstallRoot, Branding.ExeName));
            log("      desktop buttons created: 'Token Stack' (whole stack) + a 'Token Stack " +
                "Controls' folder with per-layer toggles (Headroom/RTK/Semble)");
        }
        catch (Exception ex) { log($"      (desktop shortcuts skipped: {ex.Message})"); }

        log("[8/8] save config");
        if (persistConfig) ConfigStore.Save(cfg, ConfigStore.DefaultPath);
        else log("      (component-scoped run — config.json left untouched)");

        log("DONE. Fully quit Claude Desktop from the system tray and relaunch " +
            "(env inheritance happens at process creation).");
    }

    /// <summary>Reads the user's CURRENT Claude Code base URL (settings.json env, then the
    /// User-scope env var) and adopts a vendor/custom upstream BEFORE routing rewrites the
    /// base URL to the local proxy. Idempotent: a base URL already pointing at our proxy
    /// preserves the previously-adopted upstream.</summary>
    public void DetectUpstream(StackConfig cfg)
    {
        string? current = null;
        try
        {
            var settings = new ClaudeFileEditor(SettingsPath).Load();
            current = settings["env"]?["ANTHROPIC_BASE_URL"]?.GetValue<string>();
        }
        catch { /* missing/corrupt settings = nothing to adopt */ }
        current ??= env.GetUser("ANTHROPIC_BASE_URL");

        cfg.Headroom.UpstreamUrl = ProviderDetection.ResolveUpstream(
            current, cfg.Headroom.UpstreamUrl, RoutingManager.ProxyUrl(cfg.Headroom.Port));
    }

    /// <summary>All Claude-file edits in one editor session per file = one backup per run.
    /// Also the convergence target for doctor's drift fix.</summary>
    public void ApplyClaudeWiring(StackConfig cfg)
    {
        var settingsEditor = new ClaudeFileEditor(SettingsPath);
        var settings = settingsEditor.Load();
        var changed = false;

        if (cfg.Rtk.Enabled)
            changed |= ClaudeSurgeon.EnsureRtkHook(settings,
                Path.Combine(cfg.InstallRoot, "rtk", "rtk.exe"), cfg.Rtk.HookMatcher);
        else
            changed |= ClaudeSurgeon.RemoveRtkHook(settings);

        if (cfg.Cco.Enabled)
            changed |= ClaudeSurgeon.EnsureCcoHooks(settings, CcoComponent.ReadCacheJs(cfg));
        else
            changed |= ClaudeSurgeon.RemoveCcoHooks(settings);

        if (cfg.Hooks.SessionStatusLine)
            changed |= ClaudeSurgeon.EnsureSessionStatusHook(settings,
                Path.Combine(cfg.InstallRoot, Branding.ExeName));
        else
            changed |= ClaudeSurgeon.RemoveSessionStatusHook(settings);

        if (cfg.Routing.Cli && cfg.Headroom.Enabled) // routing only makes sense when the proxy is on
            changed |= ClaudeSurgeon.SetEnvBaseUrl(settings, RoutingManager.ProxyUrl(cfg.Headroom.Port));
        else
            changed |= ClaudeSurgeon.RemoveEnvBaseUrl(settings);

        if (changed) settingsEditor.SaveWithBackup(settings);

        var claudeJsonEditor = new ClaudeFileEditor(ClaudeJsonPath);
        var claudeJson = claudeJsonEditor.Load();
        var changed2 = cfg.Semble.Enabled
            ? ClaudeSurgeon.EnsureSembleMcp(claudeJson, SembleComponent.ExePath())
            : ClaudeSurgeon.RemoveSembleMcp(claudeJson);
        if (changed2) claudeJsonEditor.SaveWithBackup(claudeJson);
    }

    /// <summary>The SessionStart hook points at installRoot\token-stack.exe, so the exe must
    /// live there — `install` copies the running binary in (self-install). The user can then
    /// delete the unzipped download without breaking the hook.</summary>
    private void CopySelfToRoot(StackConfig cfg)
    {
        var self = Environment.ProcessPath;
        var target = Path.Combine(cfg.InstallRoot, Branding.ExeName);
        if (self is null) { log("      WARN: cannot resolve own path — hook may point at a missing exe"); return; }
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return; // already running from installRoot
        try
        {
            File.Copy(self, target, overwrite: true);
            log($"      copied self to {target}");
            // Upgrade from a v1.0.x install: drop the old-named exe (the hook/shortcuts were
            // already rewritten to the new name). Harmless orphan if it's locked.
            var legacy = Path.Combine(cfg.InstallRoot, Branding.LegacyExeName);
            if (File.Exists(legacy) && !string.Equals(legacy, target, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(legacy); } catch { /* locked = leave it */ }
        }
        catch (IOException) // target locked (e.g. a hook is running it right now)
        {
            log("      self-copy skipped (target in use) — existing copy kept");
        }
    }

    /// <summary>Recognize the hand-built reference layout and announce adoption (spec §5.0):
    /// our Ensure* calls rewrite the legacy entries; the old root is left for manual delete.</summary>
    private void DetectLegacyInstall()
    {
        if (!File.Exists(SettingsPath)) return;
        var text = File.ReadAllText(SettingsPath);
        if (text.Contains("ensure-stack.ps1") || text.Contains("proxy tokens"))
            log("      detected a hand-built token stack — adopting (hooks/task/MCP will be " +
                "rewired to the managed layout; the old folder is left for you to delete).");
    }

    /// <summary>Directories `purge` deletes, in order. The HuggingFace cache is deliberately NOT
    /// here: it is a shared location other AI tooling also populates, so wiping it would delete
    /// models we never downloaded. Headroom's own models live inside it and are left behind.</summary>
    public static IEnumerable<string> PurgePaths(StackConfig cfg)
    {
        yield return cfg.InstallRoot;
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-context-optimizer");
    }

    /// <summary>Delete a directory that may hold the RUNNING exe. Windows refuses to unlink a file
    /// that is mapped into a live process, so the caller's own binary is renamed aside first and
    /// left for the OS to clean up on next boot; everything else goes now.</summary>
    private void PurgeDirectory(string dir)
    {
        if (!Directory.Exists(dir)) { log($"      {dir} (already absent)"); return; }

        var self = Environment.ProcessPath;
        if (self is not null && self.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
        {
            // Rename rather than delete: the file is locked, but a rename releases the NAME so the
            // directory can be recreated by a future install without colliding.
            try { File.Move(self, self + $".delete-me-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false); }
            catch { /* best effort — the sweep below just skips it */ }
        }

        var failed = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
            catch { failed++; }
        }
        try { Directory.Delete(dir, recursive: true); }
        catch { failed++; }

        log(failed == 0
            ? $"      removed {dir}"
            : $"      removed {dir} ({failed} item(s) still locked — they vanish after Claude and " +
              "this window close; delete the folder then if it lingers)");
    }

    public void Uninstall(StackConfig cfg, bool keepConfig, bool purge = false)
    {
        log("stopping + unregistering HeadroomProxy task");
        new HeadroomComponent(runner, port, http).Unwire(cfg);

        log("removing routing env vars");
        new RoutingManager(env).RemoveDesktop();

        log("unwiring claude files (hooks, env, MCP)");
        var settingsEditor = new ClaudeFileEditor(SettingsPath);
        var settings = settingsEditor.Load();
        var changed = ClaudeSurgeon.RemoveRtkHook(settings);
        changed |= ClaudeSurgeon.RemoveCcoHooks(settings);
        changed |= ClaudeSurgeon.RemoveSessionStatusHook(settings);
        changed |= ClaudeSurgeon.RemoveEnvBaseUrl(settings);
        if (changed) settingsEditor.SaveWithBackup(settings);

        var cjEditor = new ClaudeFileEditor(ClaudeJsonPath);
        var cj = cjEditor.Load();
        if (ClaudeSurgeon.RemoveSembleMcp(cj)) cjEditor.SaveWithBackup(cj);

        log("uninstalling semble tool + removing rtk from PATH");
        try { new SembleComponent(runner).Unwire(new Bootstrap(runner).EnsureUv()); }
        catch { log("  (uv unavailable — skipped `uv tool uninstall semble`)"); }
        new RtkComponent(runner, env).Unwire(cfg);

        try { new ShortcutCreator(runner).Remove(); log("removed desktop button"); } catch { }

        if (!keepConfig && File.Exists(ConfigStore.DefaultPath))
            File.Delete(ConfigStore.DefaultPath);

        if (purge)
        {
            log("purging installed files");
            foreach (var dir in PurgePaths(cfg)) PurgeDirectory(dir);
            log("NOTE: uv + Python (%USERPROFILE%\\.local) and the HuggingFace model cache are " +
                "shared with other tools, so they are left alone on purpose.");
        }
        else
        {
            log($"NOTE: {cfg.InstallRoot} (venv/rtk/cco/launcher) left on disk — re-run with " +
                "--purge to delete it too.");
            log("NOTE: %USERPROFILE%\\.claude-context-optimizer (read-cache data) left on disk — " +
                "--purge removes it.");
        }
    }
}
