using TokenStack.Core.Claude;
using TokenStack.Core.Config;
using TokenStack.Core.Windows;

namespace TokenStack.Core.Components;

public sealed record LayerState(bool Headroom, bool Rtk, bool Semble, bool Cco);

/// <summary>Wires/unwires each layer to match the config's enabled flags — WITHOUT
/// installing or uninstalling anything (the bits stay on disk, so on/off is instant).
/// Headroom off = stop proxy + drop routing (Claude talks direct, stays working); rtk off =
/// remove the PreToolUse hook; semble off = remove the MCP entry; cco off = remove the
/// read-cache hooks. The SessionStart status hook is deliberately left untouched so the user
/// still gets the status line when off.</summary>
public sealed class ToggleService(
    IProcessRunner runner, IEnvStore env, string settingsPath, string claudeJsonPath)
{
    public LayerState ApplyWiring(StackConfig cfg)
    {
        // 1. Proxy task: on = enable + start; off = stop + disable, so the logon
        //    trigger can't relaunch the proxy after a reboot (only if it's registered).
        var tasks = new ScheduledTaskManager(runner);
        if (tasks.Exists())
        {
            if (cfg.Headroom.Enabled) { tasks.Enable(); tasks.Start(); }
            else { tasks.Stop(); tasks.Disable(); }
        }

        // 2. Desktop routing (User env var) — present only when Headroom is on.
        var routing = new RoutingManager(env);
        if (cfg.Headroom.Enabled && cfg.Routing.Desktop) routing.ApplyDesktop(cfg.Headroom.Port);
        else routing.RemoveDesktop();

        // 3. settings.json — rtk hook + cco read-cache hooks + CLI routing env.
        var settingsEditor = new ClaudeFileEditor(settingsPath);
        var settings = settingsEditor.Load();
        var changed = cfg.Rtk.Enabled
            ? ClaudeSurgeon.EnsureRtkHook(settings,
                Path.Combine(cfg.InstallRoot, "rtk", "rtk.exe"), cfg.Rtk.HookMatcher,
                bare: RtkComponent.BareHookIsSafe(env, cfg))
            : ClaudeSurgeon.RemoveRtkHook(settings);
        changed |= cfg.Cco.Enabled
            ? ClaudeSurgeon.EnsureCcoHooks(settings,
                Path.Combine(cfg.InstallRoot, "cco", "src", "read-cache.js"))
            : ClaudeSurgeon.RemoveCcoHooks(settings);
        changed |= (cfg.Headroom.Enabled && cfg.Routing.Cli)
            ? ClaudeSurgeon.SetEnvBaseUrl(settings, RoutingManager.ProxyUrl(cfg.Headroom.Port))
            : ClaudeSurgeon.RemoveEnvBaseUrl(settings);
        if (changed) settingsEditor.SaveWithBackup(settings);

        // 4. .claude.json — semble MCP entry.
        var claudeEditor = new ClaudeFileEditor(claudeJsonPath);
        var claudeJson = claudeEditor.Load();
        var changed2 = cfg.Semble.Enabled
            ? ClaudeSurgeon.EnsureSembleMcp(claudeJson, SembleComponent.ExePath())
            : ClaudeSurgeon.RemoveSembleMcp(claudeJson);
        if (changed2) claudeEditor.SaveWithBackup(claudeJson);

        return new LayerState(cfg.Headroom.Enabled, cfg.Rtk.Enabled, cfg.Semble.Enabled, cfg.Cco.Enabled);
    }
}
