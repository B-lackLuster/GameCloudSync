# GameCloudSync — Cloud Save Manager

[![CI](https://github.com/B-lackLuster/GameCloudSync/actions/workflows/ci.yml/badge.svg)](https://github.com/B-lackLuster/GameCloudSync/actions/workflows/ci.yml)

A Windows desktop app (C# / WPF, .NET 9) that monitors game save folders and backs them up to OneDrive and/or WebDAV.

## Features

- Auto monitoring: saves are backed up 5 seconds after the folder stops changing; a save made during a running backup is queued, not lost; unchanged saves are skipped
- Dual cloud targets: OneDrive and WebDAV, either or both
- Several PCs: each game remembers when this PC last synced it; if both this PC and the cloud changed since then, you are asked which one to keep instead of "the newer one wins"
- Backup history: browse, restore, delete, and batch-download cloud backups; a restore shows the local and the backup time first
- Safe restores: the local save is snapshotted before it is overwritten (latest 5 kept) and can be restored at any time; a backup only appears once fully written, so a failed or interrupted backup is never taken for the newest one
- Cloud config sync: config is backed up to the cloud, new games are detected on startup and merged with one click
- Batch operations: upload, overwrite-download, delete, export/import configs; the game being processed is shown and the job can be cancelled
- System integration: tray icon, auto-start with Windows (straight to the tray), single-instance wake-up
- Failures stay visible: a failed backup raises a Windows notification, turns the tray icon red and is explained on the dashboard and the game card (login refused, path missing, WebDAV unreachable…); failed backups are retried automatically, and so are WebDAV requests that hit a network hiccup
- Run log: the full log is written to the `logs` folder (about the last 2 MB), one click away on the Activity page
- Input checks: save folders that are far too broad (a whole drive, the user profile, Documents) are refused, the WebDAV address is checked before settings are saved, and game names are checked as you type
- Save paths may be written like `%APPDATA%\Game\Saves`; configs imported from another PC are switched to this PC's user folder
- Secure storage: WebDAV password encrypted with DPAPI, never written to config
- Bilingual UI: switch between Chinese and English anytime in Settings
- Shortcuts: F5 refreshes, Ctrl+N adds a game, Del deletes the selected games

## Usage

1. Download `GameCloudSync.exe` from [Releases](https://github.com/B-lackLuster/GameCloudSync/releases) and run it — no .NET installation required.
2. Existing users: put your old `config.json` next to the exe; credentials are migrated automatically.
3. Configure the OneDrive cloud root (or click "Detect") and/or the WebDAV server in Settings, add games, and start monitoring.

Config, logs and snapshots live next to the exe by default (portable). If that folder can't be written (for example under Program Files), they go to `%APPDATA%\GameCloudSync` instead, taking a config already beside the exe along.

## Project layout

- `GameCloudSync.CSharp/src/GameCloudSync.Core`: core logic (config, credentials, WebDAV, backup and restore, monitoring, cloud config)
- `GameCloudSync.CSharp/src/GameCloudSync.App`: WPF UI
- `GameCloudSync.CSharp/tests`: xUnit tests, run on Windows by GitHub Actions on every push

## Build

Requires .NET SDK 9:

```powershell
dotnet build GameCloudSync.CSharp/GameCloudSync.sln
dotnet test GameCloudSync.CSharp/GameCloudSync.sln
dotnet publish GameCloudSync.CSharp/src/GameCloudSync.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o GameCloudSync.CSharp/publish
```

The result is `GameCloudSync.CSharp/publish/GameCloudSync.exe` (a self-contained single file; the target PC needs no .NET).

## Compatibility

- `config.json` fields are fully compatible with the old Python version
- Backup folder naming is unchanged; old and new backups are visible to both versions
- The early Python version was removed from the repository; it is still in the git history (last in commit `a8bb92f`)
- Requires Windows 10 / 11
