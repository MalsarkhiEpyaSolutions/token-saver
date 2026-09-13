# TokenSaver

One executable that installs and manages the 4-layer Claude Code token-optimization
stack on Windows: **Headroom** (API-payload compression proxy), **RTK** (Bash
command-output filter), **Semble** (semantic code-search MCP), **CCO** (read-cache:
blocks redundant file re-reads). Measured combined savings: ~74-95% depending on
workload. Also saves tokens on Claude Code pointed at GLM (Z.ai), Kimi (Moonshot),
or MiniMax — see below.

## Quick start — online (any Windows 10/11 machine, no admin)

In a **regular** terminal (Start menu → Windows Terminal or PowerShell — *not* a terminal
spawned by Claude Desktop, which sandboxes profile writes):

```powershell
irm https://raw.githubusercontent.com/MalsarkhiEpyaSolutions/token-saver/master/install.ps1 | iex
```

That downloads the latest release and runs `install` (pinned components; Headroom
cold-loads 25-105s). No SmartScreen prompt — the exe arrives without a Mark-of-the-Web.
Then fully quit Claude (Desktop: tray icon → Quit) and relaunch.

<details><summary>Manual download instead</summary>

1. Download the latest `token-saver-v*.zip` from
   [Releases](https://github.com/MalsarkhiEpyaSolutions/token-saver/releases),
   unzip anywhere, open a terminal next to `token-saver.exe`.
   *A browser download marks the file, so SmartScreen warns on first run — click
   "More info" → "Run anyway" (or `Unblock-File .\token-saver.exe`).*
2. `.\token-saver.exe install`
3. Fully quit Claude (Desktop: tray icon → Quit) and relaunch.

</details>

Either way, every session then starts with:
`[TokenSaver] Headroom: up (:8787, ROUTED, reqs=N) | RTK: up | Semble: up (MCP) | CCO: up`

The installer copies itself to `C:\token-stack\token-saver.exe` — you can delete the
unzipped download afterwards. (The install directory stays `C:\token-stack` for
seamless upgrades from older versions.)

## GLM / Kimi / MiniMax / OpenRouter (Claude Code with a vendor endpoint)

If your Claude Code is already pointed at a vendor Anthropic-compatible endpoint
(`ANTHROPIC_BASE_URL` = `api.z.ai/api/anthropic`, `api.moonshot.ai/anthropic`,
`api.minimax.io/anthropic`, or `openrouter.ai/api`), `install` **detects and adopts
it**: it inserts the Headroom proxy in front and forwards to that vendor. Your vendor
API key and model are never touched — they pass straight through. The status line
then reads `ROUTED→Kimi` (or GLM / MiniMax / OpenRouter). Plain Claude Code is
unaffected.

## Run several models in parallel WITH savings (`profile`) — recommended

`.\token-saver.exe profile add` gives a model its **own compression proxy** on its own port,
so it runs in parallel with Claude and other models **and** gets token savings. It creates a
desktop launcher **and an on/off button** (in `Token Stack Controls`). Requires a prior
`install` (profiles reuse its proxy venv).

```
token-saver profile add           # pick model, paste key → proxy + launcher + on/off button
token-saver profile list          # models + ON/OFF state
token-saver profile on|off|toggle <name>
token-saver profile remove <name>
```

Each running model's proxy uses a few hundred MB RAM + one cold-load, so keep only the ones
you're using ON (that's what the buttons are for). Savings show up in `token-saver gain`.

## Quick parallel WITHOUT savings (`launcher`)

`.\token-saver.exe launcher` makes a desktop `Claude - <name>.cmd` for a chosen backend
(GLM/Kimi/MiniMax/OpenRouter, or **Custom** = any Anthropic-compatible endpoint + model +
key). Double-click it to run Claude Code on that model **in its own window** — your normal
`claude` and other launchers keep running side by side; nothing global is changed. Make one
per model to have Claude, MiniMax, OpenRouter… all open at once.

*(Claude Code speaks the Anthropic wire format, so a launcher works with any
Anthropic-compatible endpoint. OpenAI-only local servers like Ollama would need a
translation proxy — not covered here. The key is stored in the .cmd in plain text; keep it
private.)*

## CCO read-cache (4th layer)

Blocks redundant file re-reads (same file, same range, unchanged, this session) — the #1
token waste in long coding sessions. Runs as `node` hooks (PreToolUse Read, PostToolUse
Edit|Write, PreCompact) pointing at a pinned, vendored snapshot of
[`egorfedorov/claude-context-optimizer`](https://github.com/egorfedorov/claude-context-optimizer)
(read-cache only — no tracker/prompt-coach, so it never competes with the status line).
Lossless and fail-safe: any error lets the read through. The lossy "map-then-load" nudge is
disabled (`bigFileDigest:false`). **Requires Node ≥18 on PATH**; if absent, `install` skips it
with a warning (the offline bundle ships no Node). Toggle with `on|off|toggle cco`.

## Offline / air-gapped machines

The same `install` works with **zero network** when an offline bundle is present — it
**auto-detects** a `vendor\` folder next to `token-saver.exe`.

**Build the bundle once on an online machine** (after a normal online install, so rtk + the
HuggingFace models exist locally):
```powershell
.\token-saver.exe pack --out token-saver-offline-v1.1.0.zip   # ~550 MB
```
The bundle contains `uv.exe` + a portable Python + a pip **wheelhouse** (wheels are *built*
on the online machine via `pip wheel`, so the air-gapped install is pure-wheel — some
headroom-ai versions ship sdist-only and can't be built offline) + `rtk.exe` + the
HuggingFace model cache Headroom needs at runtime.

**On the air-gapped machine:** copy the zip → unzip → `.\token-saver.exe install`. It detects
`vendor\`, installs everything from the bundle, and pins HuggingFace to offline mode
(`HF_HUB_OFFLINE=1`) so the proxy never reaches the internet. Force with `--offline` /
`--online` if needed.

## Turn it on/off (one press)

The whole stack — or any single layer — pauses and resumes instantly (no reinstall):

```powershell
.\token-saver.exe off              # whole stack off  → Claude talks DIRECT to Anthropic (still works)
.\token-saver.exe on               # whole stack back on
.\token-saver.exe toggle           # flip whichever way
.\token-saver.exe off rtk          # just one layer: headroom | rtk | semble | cco
```

**OFF is safe:** it removes the routing too, so Claude keeps working directly against
`api.anthropic.com` — it doesn't just kill the proxy and strand you. OFF also disables
the proxy's logon autostart, so it stays off across reboots; ON re-enables it. Restart
Claude after toggling for it to take effect.

**The buttons:** `install` drops these on your Desktop (re-create anytime with
`.\token-saver.exe shortcut`):
- **Token Stack** (loose icon) — double-click toggles the *whole* stack on/off.
- **Token Stack Controls** (folder) — one toggle per layer: *Headroom*, *RTK*, *Semble* — so you
  can flip any single feature with a click.

Each shows a popup confirming the new state.

## Commands

| Command | What |
|---|---|
| `install` | full install/repair (idempotent; `--component headroom\|rtk\|semble`; `--offline`/`--online`) |
| `launcher` | interactive: make a desktop launcher for a model (GLM/Kimi/MiniMax/OpenRouter/custom), runs in parallel |
| `on` / `off` / `toggle` `[layer]` | pause/resume whole stack or one layer (no reinstall) |
| `shortcut` | (re)create the desktop toggle button |
| `pack` | build an offline bundle (run on an online machine) |
| `status` | live table (shows OFF for paused layers); `--hook` one-line; `--json` |
| `start` / `stop` / `restart` | proxy lifecycle (restart = zombie recovery) |
| `config list/get/set/open` | edit `%LOCALAPPDATA%\token-stack\config.json` |
| `doctor [--fix]` | detect + repair the known failure modes |
| `update --component X [--version v]` | move a component pin |
| `gain` | unified savings report |
| `uninstall [--purge] [--keep-config] [-y]` | full rollback (Claude-file backups kept). `--purge` also deletes `C:\token-stack` and the read-cache data, so nothing is left to remove by hand — uv, Python and the HuggingFace cache are shared with other tooling and are deliberately left alone |

## Config keys (full control)

`installRoot` (no spaces!) · `headroom.enabled/port/mode(token|cache|passthrough)/version/pythonVersion/extraArgs/upstreamUrl(vendor endpoint, auto-detected)`
· `rtk.enabled/version/source/hookMatcher(Bash only)` · `semble.enabled/version` · `cco.enabled/version`
· `routing.cli/desktop` · `hooks.sessionStatusLine` · `bootstrap.uvInstaller`

## Trust & safety

- No admin: user-scope scheduled task, HKCU env vars, %LOCALAPPDATA% files.
- Every edit to `~/.claude/settings.json` / `~/.claude.json` first writes a
  timestamped `*.token-stack-backup-*` sibling.
- Worried about compression fidelity? `config set headroom.mode cache` (zero
  rewriting) or `passthrough` (pure passthrough).
- If the proxy dies with desktop routing on, sessions fail until `token-stack start`
  (the task auto-restarts; `doctor --fix` repairs the rest).

Knowledge base / gotcha history: `docs/reference/TOKEN-STACK-SETUP-PROMPT.md`.
