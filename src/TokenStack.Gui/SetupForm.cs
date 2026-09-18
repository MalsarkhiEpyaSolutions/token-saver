using System.Diagnostics;
using System.Text.RegularExpressions;
using TokenStack.Core.Components;
using TokenStack.Core.Config;
using TokenStack.Core.Install;

namespace TokenStack.Gui;

/// <summary>Setup front-end for token-saver.exe. It does not install anything itself: it runs
/// `token-saver.exe install --root <path>` and renders that process's output, so the CLI stays
/// the single implementation of what "install" means and the two can never drift apart.</summary>
public sealed partial class SetupForm : Form
{
    private readonly Label _verdict = new();
    private readonly TextBox _root = new();
    private readonly Button _browse = new();
    private readonly CheckBox _outputStyle = new();
    private readonly Button _action = new();
    private readonly ProgressBar _bar = new();
    private readonly Label _status = new();
    private readonly Label _rootError = new();
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _tick = new();

    private Process? _proc;
    private readonly Stopwatch _elapsed = new();
    private string _stepText = "";
    private bool _finished;

    private static string CliPath => Path.Combine(AppContext.BaseDirectory, "token-saver.exe");

    /// <summary>Spectre strips its own markup when redirected, but a stray ANSI sequence in a
    /// child tool's output would otherwise show up as mojibake in the log box.</summary>
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    public SetupForm()
    {
        // Everything is laid out by docking panels, never by absolute Location/Size. An earlier
        // hand-placed version silently lost the Browse button off the right edge whenever the
        // form came out a different size than asked for (measured: a 760-wide request rendered
        // 509 wide on a 150% display). With Fill/AutoSize the worst case is a cramped form
        // instead of controls that exist but cannot be reached.
        Font = new Font("Segoe UI", 9F);
        Text = "TokenSaver Setup";
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(14);

        var heading = new Label
        {
            Text = "Install the Claude Code token-optimization stack",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        };
        var sub = new Label
        {
            Text = "Headroom (payload compression) · RTK (command-output filter) · "
                 + "Semble (semantic search) · CCO (read cache)",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            MaximumSize = new Size(0, 0),
            Margin = new Padding(0, 0, 0, 10),
        };
        _verdict.AutoSize = true;
        _verdict.Margin = new Padding(0, 0, 0, 10);

        var rootLabel = new Label { Text = "Install to:", AutoSize = true, Margin = new Padding(0) };

        _root.Text = ConfigStore.DefaultRoot;
        _root.Dock = DockStyle.Fill;
        _root.Margin = new Padding(0, 0, 8, 0);
        _root.TextChanged += (_, _) => ValidateRoot();

        _browse.Text = "Browse…";
        _browse.AutoSize = true;
        _browse.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _browse.Margin = new Padding(0);
        _browse.Click += OnBrowse;

        var pathRow = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.Controls.Add(_root, 0, 0);
        pathRow.Controls.Add(_browse, 1, 0);

        _rootError.AutoSize = true;
        _rootError.ForeColor = Color.Firebrick;
        _rootError.Margin = new Padding(0, 0, 0, 4);

        _outputStyle.Text = "Also use the “Concise Plus” output style (shorter answers; "
                          + "suggestion lists capped at 5)";
        _outputStyle.AutoSize = true;
        _outputStyle.Margin = new Padding(0, 0, 0, 8);

        _action.Text = "Install";
        _action.AutoSize = true;
        _action.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _action.MinimumSize = new Size(110, 30);
        _action.Margin = new Padding(0, 0, 10, 0);
        _action.Click += OnAction;

        _bar.Maximum = 100;
        _bar.Dock = DockStyle.Fill;
        _bar.Margin = new Padding(0, 4, 0, 4);

        var actionRow = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.Controls.Add(_action, 0, 0);
        actionRow.Controls.Add(_bar, 1, 0);

        _status.AutoSize = true;
        _status.ForeColor = SystemColors.GrayText;
        _status.Text = "Ready.";
        _status.Margin = new Padding(0, 0, 0, 6);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.WordWrap = false;
        _log.BackColor = Color.FromArgb(24, 24, 24);
        _log.ForeColor = Color.Gainsboro;
        _log.Font = new Font("Consolas", 9F);
        _log.Margin = new Padding(0);

        var layout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (ctl, fill) in new (Control, bool)[]
                 {
                     (heading, false), (sub, false), (_verdict, false),
                     (rootLabel, false), (pathRow, false),
                     (_rootError, false), (_outputStyle, false), (actionRow, false),
                     (_status, false), (_log, true),
                 })
        {
            layout.RowStyles.Add(fill
                ? new RowStyle(SizeType.Percent, 100)
                : new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(ctl, 0, layout.RowStyles.Count - 1);
        }
        layout.RowCount = layout.RowStyles.Count;
        Controls.Add(layout);

        _tick.Interval = 500;
        _tick.Tick += (_, _) => _status.Text = $"{_stepText}  ({_elapsed.Elapsed:mm\\:ss} elapsed)";

        CheckExisting();
        ValidateRoot();
        if (!File.Exists(CliPath))
        {
            _rootError.Text = $"token-saver.exe is missing from {AppContext.BaseDirectory} — "
                            + "unzip the whole download and run setup from that folder.";
            _action.Enabled = false;
        }
    }

    /// <summary>Look before installing: report what is already on this machine and relabel the
    /// button, so someone who is already current is told so instead of being walked through a
    /// multi-minute rebuild they did not need.</summary>
    private void CheckExisting()
    {
        var snap = InstallState.Inspect(Branding.Version);
        if (snap.Root is { Length: > 0 }) _root.Text = snap.Root;

        // On by default for a newcomer, but a recorded "no" is never quietly overturned.
        _outputStyle.Checked = snap.OutputStyleEnabled ?? true;

        (_verdict.Text, _verdict.ForeColor, _action.Text) = snap.Verdict switch
        {
            InstallVerdict.NotInstalled => (
                $"No existing install found — this installs TokenSaver {snap.SetupVersion}.",
                SystemColors.GrayText, "Install"),

            InstallVerdict.UpToDate => (
                $"TokenSaver {snap.InstalledVersion} is already installed at {snap.Root}. "
                + "Nothing to update — use the button only to repair it.",
                Color.ForestGreen, "Reinstall / Repair"),

            InstallVerdict.UpdateAvailable => (
                $"TokenSaver {snap.InstalledVersion} is installed at {snap.Root}. "
                + $"This setup updates it to {snap.SetupVersion}.",
                Color.FromArgb(0, 90, 158), "Update"),

            _ => (
                $"A stack is installed at {snap.Root} but did not record its version. "
                + $"This setup brings it to {snap.SetupVersion}.",
                Color.DarkGoldenrod, "Update"),
        };
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose where the stack is installed (venv, models, rtk, cco — a few GB)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        try { dlg.SelectedPath = _root.Text; } catch { /* unparsable text = start wherever */ }
        if (dlg.ShowDialog(this) == DialogResult.OK) _root.Text = dlg.SelectedPath;
    }

    /// <summary>Same rules the installer itself enforces (absolute, no spaces, drive present),
    /// so a bad path is caught here instead of minutes into building a venv.</summary>
    private bool ValidateRoot()
    {
        var errors = ConfigValidator
            .Validate(StackConfig.CreateDefault(_root.Text.Trim()))
            .Where(e => e.StartsWith("installRoot", StringComparison.Ordinal))
            .ToList();

        _rootError.Text = errors.FirstOrDefault() ?? "";
        var ok = errors.Count == 0;
        if (!_finished && _proc is null) _action.Enabled = ok && File.Exists(CliPath);
        return ok;
    }

    private void OnAction(object? sender, EventArgs e)
    {
        if (_finished) { Close(); return; }
        if (_proc is not null || !ValidateRoot()) return;
        StartInstall();
    }

    private void StartInstall()
    {
        var args = $"install --root \"{_root.Text.Trim()}\""
                 + (_outputStyle.Checked ? " --output-style" : "");

        var psi = new ProcessStartInfo(CliPath, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Spectre writes colour only when it thinks it has a terminal; make that explicit so no
        // escape sequences reach the log box.
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TERM"] = "dumb";

        try
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, ev) => Append(ev.Data);
            _proc.ErrorDataReceived += (_, ev) => Append(ev.Data);
            _proc.Exited += (_, _) => BeginInvoke(Finish);
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _proc = null;
            Fail($"could not start {CliPath}: {ex.Message}");
            return;
        }

        _root.Enabled = _browse.Enabled = _outputStyle.Enabled = false;
        _action.Enabled = false;
        _bar.Value = 0;
        _log.Clear();
        _stepText = "starting…";
        _elapsed.Restart();
        _tick.Start();
    }

    private void Append(string? raw)
    {
        if (raw is null) return;                       // end-of-stream marker
        var line = AnsiEscape().Replace(raw, "").TrimEnd();
        if (line.Length == 0) return;

        BeginInvoke(() =>
        {
            _log.AppendText(line + Environment.NewLine);

            if (StepProgress.Parse(line) is not { } p) return;
            // Fill to the step just COMPLETED, not the one starting, so the bar never claims
            // work that is still running.
            _bar.Value = StepProgress.Percent(p.Step - 1, p.Total);
            _stepText = line.Trim();
        });
    }

    private void Finish()
    {
        _tick.Stop();
        _elapsed.Stop();
        var code = _proc?.ExitCode ?? -1;
        _proc?.Dispose();
        _proc = null;

        if (code == 0)
        {
            _bar.Value = 100;
            _status.ForeColor = Color.ForestGreen;
            _status.Text = $"Done in {_elapsed.Elapsed:mm\\:ss}. "
                         + "Fully quit Claude (Desktop: tray icon → Quit) and relaunch.";
            CheckExisting();          // banner now reads "already installed / nothing to update"
            _finished = true;
            _action.Text = "Close";
            _action.Enabled = true;
        }
        else
        {
            Fail($"install exited with code {code}. The log above shows the failing step; "
               + "re-running Install resumes from it.");
        }
    }

    private void Fail(string message)
    {
        _tick.Stop();
        _status.ForeColor = Color.Firebrick;
        _status.Text = message;
        _root.Enabled = _browse.Enabled = _outputStyle.Enabled = true;
        _action.Text = "Retry";
        _action.Enabled = true;
    }

    /// <summary>Closing mid-install would leave a half-wired stack behind with no window to
    /// report it, so make the user confirm and kill the child before going.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_proc is { HasExited: false })
        {
            var stop = MessageBox.Show(this,
                "The install is still running. Stop it and quit?\n\n"
                + "The stack will be left half-installed; re-running setup repairs it.",
                "TokenSaver Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (stop != DialogResult.Yes) { e.Cancel = true; return; }
            try { _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        base.OnFormClosing(e);
    }
}
