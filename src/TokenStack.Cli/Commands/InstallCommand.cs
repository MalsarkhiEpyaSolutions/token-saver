using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using TokenStack.Core.Components;
using TokenStack.Core.Config;
using TokenStack.Core.Install;

namespace TokenStack.Cli.Commands;

public sealed class InstallCommand : Command<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--component <NAME>")]
        public string? Component { get; init; }

        [CommandOption("--output-style")]
        [Description("Enable the optional 'Concise Plus' output style without being asked " +
                     "(for the setup GUI and unattended installs). Never turns it off — " +
                     "omitting it leaves an earlier choice alone.")]
        public bool OutputStyle { get; init; }

        [CommandOption("--root <PATH>")]
        [Description("Install directory, e.g. D:\\token-stack. Absolute, no spaces. " +
                     "Default C:\\token-stack; a fresh interactive install asks.")]
        public string? Root { get; init; }

        [CommandOption("--offline")]
        public bool Offline { get; init; }

        [CommandOption("--online")]
        public bool Online { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var cfg = Services.LoadConfigOrDefault();
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;
        var fresh = !File.Exists(ConfigStore.DefaultPath);

        // --root always wins. Otherwise only a FRESH interactive install asks: re-running install
        // on an existing stack must keep the root it already has, unattended runs must not block.
        var root = settings.Root ?? (fresh && interactive && settings.Component is null
            ? AskRoot(cfg.InstallRoot)
            : null);
        if (root is not null) cfg.InstallRoot = root.Trim();

        // Only ever turns it ON: a re-install without the flag must not silently undo a yes.
        if (settings.OutputStyle) cfg.OutputStyle.Enabled = true;

        if (ConfigValidator.Validate(cfg) is { Count: > 0 } bad)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{string.Join("; ", bad)}[/]");
            return 1;
        }

        if (settings.Component is { } only)
        {
            // narrow THIS RUN only — persistConfig:false below keeps config.json intact
            cfg.Headroom.Enabled = cfg.Headroom.Enabled && only == "headroom";
            cfg.Rtk.Enabled = cfg.Rtk.Enabled && only == "rtk";
            cfg.Semble.Enabled = cfg.Semble.Enabled && only == "semble";
        }

        // --offline = require a vendor bundle; --online = force download; neither = auto-detect.
        bool? force = settings.Offline ? true : settings.Online ? false : null;
        InstallSource src;
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
            src = InstallSourceResolver.Resolve(exeDir, force);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }

        var pipeline = new InstallPipeline(
            Services.Runner, Services.Env, Services.Port, Services.Http,
            msg => AnsiConsole.MarkupLineInterpolated($"[grey]{msg}[/]"))
        {
            // Only a real terminal doing a full install gets the one opt-in prompt. A piped
            // `irm | iex`, CI, or a `--component` repair passes null and is never blocked.
            Confirm = settings.Component is null && AnsiConsole.Profile.Capabilities.Interactive
                ? prompt => AnsiConsole.Confirm(prompt, defaultValue: false)
                : null,
        };
        try
        {
            pipeline.Run(cfg, persistConfig: settings.Component is null, source: src);
            AnsiConsole.MarkupLine("[green]Install complete.[/] Run [bold]token-stack status[/] after restarting Claude.");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Install failed:[/] {ex.Message}");
            AnsiConsole.MarkupLine("[yellow]Re-running `token-stack install` resumes from the failed step.[/]");
            return 1;
        }
    }

    /// <summary>Ask once, validating with the SAME rules the config validator enforces, so a bad
    /// answer is rejected at the prompt instead of minutes into the install.</summary>
    private static string AskRoot(string current)
    {
        AnsiConsole.MarkupLine(
            "[grey]Where should the stack install? (venv, models, rtk, cco — a few GB)[/]");
        return AnsiConsole.Prompt(
            new TextPrompt<string>("[grey]      install directory:[/]")
                .DefaultValue(current)
                .Validate(candidate =>
                {
                    var probe = StackConfig.CreateDefault(candidate.Trim());
                    var errs = ConfigValidator.Validate(probe)
                        .Where(e => e.StartsWith("installRoot", StringComparison.Ordinal)).ToList();
                    return errs.Count == 0 ? ValidationResult.Success() : ValidationResult.Error(errs[0]);
                }));
    }
}
