# GameCloudSync (C# / WPF)

A Windows desktop app that monitors game saves and backs them up to OneDrive and/or WebDAV.

## Features

- Auto backup on save changes (after the folder goes quiet; changes during a backup are queued; unchanged saves are skipped)
- OneDrive / WebDAV backup and restore
- Backup history: browse, restore, delete, batch download
- Pre-restore snapshots of the local save; backups are published only once complete, so failures leave no partial backup
- Cloud config backup, new-game detection, one-click merge
- Tray icon, auto-start, single-instance wake-up
- WebDAV password encrypted with DPAPI
- Bilingual UI (switch in Settings)

## Structure

- `src/GameCloudSync.Core`: business core (config, credentials, WebDAV, backup/restore, monitoring, cloud config)
- `src/GameCloudSync.App`: WPF UI
- `tests/`: xUnit unit tests

## Build / Test / Publish

```powershell
dotnet build GameCloudSync.sln
dotnet test GameCloudSync.sln
dotnet publish src/GameCloudSync.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Output: `publish/GameCloudSync.exe` (self-contained single file, no .NET required on the target machine).

## Compatibility

- Reads the old `config.json` directly; existing credentials are migrated automatically
- Backup folder naming is unchanged; old and new backups are visible to both versions
- Requires Windows 10 / 11
