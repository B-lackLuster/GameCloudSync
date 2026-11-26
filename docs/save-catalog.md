# Save catalog

The embedded `save-catalog.json.gz` combines save paths from the `data` branch of [Ludusavi Manifest](https://github.com/mtkennerly/ludusavi-manifest) with Chinese game names from [Wikidata](https://www.wikidata.org/wiki/Wikidata:Data_access/en) (CC0) and a hand-maintained alias list. The catalog records the Ludusavi Git revision. The Ludusavi upstream license is bundled in the Core assembly and stored alongside the snapshot.

To refresh the catalog, update a local clone of the upstream `data` branch, install PyYAML in your Python environment, and run from the GameCloudSync repository root:

```text
python scripts/build-save-catalog.py <path-to-ludusavi-manifest-checkout> --report build/save-catalog-wikidata-report.json
```

By default this reuses the checked-in Wikidata snapshot (`save-catalog-wikidata.json.gz`). Add `--refresh-wikidata` to query Wikidata for the catalog's public Steam App IDs. The script imports Chinese labels and aliases only where an App ID connects one Wikidata item to one catalog game. Ambiguous IDs and items shared across editions go into the report for review. The report also lists Chinese names shared by different games. Search keeps those games as separate results.

Use the Ludusavi canonical title as the key in `GameCloudSync.CSharp/src/GameCloudSync.Core/Assets/save-catalog-zh.json`. The first name is the Chinese display name and takes precedence over Wikidata. Subsequent names are searchable aliases. Regenerate the catalog after editing the file. No network request is made when users search in the app.

The application checks local candidates only when a game is selected. Install relative rules use the selected Steam or manually chosen install folder. Exact directory rules can be selected if they contain files. File matches can be viewed for reference because GameCloudSync restores a whole directory at a time.

For Ubisoft Connect, the finder checks registered and default launcher locations for the catalog's numeric game directory under each account in `savegames`. A Steam installation uses the game IDs listed for Steam. The general catalog result can show multiple accounts or editions. For Assassin's Creed Shadows it also checks `%APPDATA%\Goldberg UplayEmu Saves\8006` for a game-specific save file. The options file alone does not qualify. Each matching folder is listed separately with its source.
