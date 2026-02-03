# costats

A lightweight Windows tray app that shows live status for AI coding providers like Codex and Claude Code, plus token usage and spend.

## What it shows
- Session and weekly utilization with reset timers and pace indicators.
- Daily tokens + cost and 30-day rolling tokens + cost.
- Overage or credit balance when available.
- One-tap tray widget and a global hotkey (e.g., Alt+S).

## Install

**One-step PowerShell (technical)**
```powershell
iwr -useb https://raw.githubusercontent.com/fmdz387/costats/master/scripts/install.ps1 | iex
```
Downloads the latest release, installs per-user and creates a Start Menu shortcut.

**From source:** see **Build** below.

## Usage
- Click the tray icon to open the widget.
- Press `Alt+S` to toggle the widget (configurable; default is `Alt+S`).
- Open Settings to set refresh interval or start at login.

## Configuration
Settings are stored at:
`%LOCALAPPDATA%\costats\settings.json`

Common settings:
- `RefreshMinutes` (default 5)
- `Hotkey` (e.g., `Alt+S`)
- `StartAtLogin` (true/false)

Optional environment variable:
- `CODEX_HOME` to point to a custom Codex config/logs directory.

## Data sources
- Codex usage: OAuth usage endpoint via `~/.codex/auth.json` (or `CODEX_HOME`), with local logs as a fallback for estimates.
- Claude usage: OAuth usage endpoint via `~/.claude/.credentials.json`, with local logs as a fallback for estimates.
- Token + cost estimates: local JSONL logs from `~/.codex/sessions` and `~/.claude/projects`.

## Security & privacy
- Reads local auth and log files on your machine.
- Sends requests only to provider APIs to fetch usage data; no third-party telemetry.

## Performance
- Background polling at a fixed interval (default 5 minutes).
- Single-instance, tray-first UI designed to stay lightweight.

## Build
Requires a .NET SDK that supports `net10.0-windows`.

```powershell
# Build
dotnet build .\costats.sln -c Release

# Publish portable single-file binaries (x64 + arm64)
.\scripts\publish.ps1
```
