# GameCloudSync — Cloud Save Manager

A Windows desktop app (C# / WPF, .NET 9) that monitors game save folders and backs them up to OneDrive and/or WebDAV.

## Features

- Auto monitoring: saves are backed up 5 seconds after the folder stops changing; a save made during a running backup is queued, not lost; unchanged saves are skipped
- Dual cloud targets: OneDrive and WebDAV, either or both
- Backup history: browse, restore, delete, and batch-download cloud backups
- Safe restores: the local save is snapshotted before it is overwritten (latest 5 kept) and can be restored at any time; a backup only appears once fully written, so a failed or interrupted backup is never taken for the newest one
- Cloud config sync: config is backed up to the cloud, new games are detected on startup and merged with one click
- Batch operations: upload, overwrite-download, delete, export/import configs
- System integration: tray icon, auto-start with Windows, single-instance wake-up
- Secure storage: WebDAV password encrypted with DPAPI, never written to config
- Bilingual UI: switch between Chinese and English anytime in Settings

## Usage

1. Download `GameCloudSync.exe` from [Releases](https://github.com/B-lackLuster/GameCloudSync/releases) and run it — no .NET installation required.
2. Existing users: put your old `config.json` next to the exe; credentials are migrated automatically.
3. Configure the OneDrive cloud root and/or WebDAV server in Settings, add games, and start monitoring.

## Build

Requires .NET SDK 9:

```powershell
dotnet build GameCloudSync.CSharp/GameCloudSync.sln
dotnet test GameCloudSync.CSharp/GameCloudSync.sln
dotnet publish GameCloudSync.CSharp/src/GameCloudSync.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o GameCloudSync.CSharp/publish
```

## Compatibility

- `config.json` fields are fully compatible with the old Python version
- Backup folder naming is unchanged; old and new backups are visible to both versions
- Requires Windows 10 / 11
