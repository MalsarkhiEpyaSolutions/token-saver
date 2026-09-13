using Spectre.Console;
using Spectre.Console.Cli;
using TokenStack.Core.Install;

namespace TokenStack.Cli.Commands;

public sealed class UninstallCommand : Command<UninstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--keep-config")] public bool KeepConfig { get; init; }

        [CommandOption("--purge")]
        [Description("Also delete the install folder and the read-cache data — nothing left to remove by hand.")]
        public bool Purge { get; init; }

        [CommandOption("-y|--yes")] public bool Yes { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var what = settings.Purge
            ? "Remove the token stack AND delete its installed files (task, env vars, hooks, MCP entry, C:\token-stack)?"
            : "Remove the token stack (task, env vars, hooks, MCP entry)?";
        if (!settings.Yes && !AnsiConsole.Confirm(what, false)) return 1;

        var cfg = Services.LoadConfigOrDefault();
        new InstallPipeline(Services.Runner, Services.Env, Services.Port, Services.Http,
                m => AnsiConsole.MarkupLineInterpolated($"[grey]{m}[/]"))
            .Uninstall(cfg, settings.KeepConfig, settings.Purge);

        AnsiConsole.MarkupLine(settings.Purge
            ? "[green]Uninstalled and purged.[/] Claude config backups (*.token-stack-backup-*) were kept."
            : "[green]Uninstalled.[/] Claude config backups were kept. Re-run with [bold]--purge[/] to delete the files too.");
        return 0;
    }
}
