"""Build the offline Windows save catalog from a checkout of ludusavi-manifest's data branch."""

import argparse
import gzip
import json
import subprocess
from pathlib import Path

import yaml

from wikidata_names import fetch_snapshot, load_snapshot, merge_names


ASSETS = Path(__file__).resolve().parents[1] / "GameCloudSync.CSharp/src/GameCloudSync.Core/Assets"


def windows_stores(rule):
    conditions = rule.get("when") or [{}]
    stores = {clause.get("store", "") for clause in conditions if clause.get("os") in (None, "windows")}
    return sorted(stores) if "" not in stores else [""]


def build_catalog(source, chinese_aliases):
    names = {}
    for name, entry in source.items():
        if "alias" in entry:
            target = entry["alias"]
            visited = {name}
            while target in source and "alias" in source[target] and target not in visited:
                visited.add(target)
                target = source[target]["alias"]
            if target not in visited and target in source:
                names.setdefault(target, []).append(name)

    catalog = []
    for title, entry in source.items():
        if "alias" in entry:
            continue
        rules = []
        for path, rule in (entry.get("files") or {}).items():
            stores = windows_stores(rule)
            if "save" in rule.get("tags", []) and stores:
                rules.append({"Path": path, "Stores": stores})
        has_registry = any("save" in rule.get("tags", []) for rule in (entry.get("registry") or {}).values())
        if not rules and not has_registry:
            continue
        ids = set((entry.get("id") or {}).get("steamExtra") or [])
        steam_id = (entry.get("steam") or {}).get("id")
        if steam_id:
            ids.add(steam_id)
        catalog.append({"Title": title, "ChineseNames": chinese_aliases.get(title, []),
                        "Aliases": sorted(set(names.get(title, []))), "SteamIds": sorted(ids),
                        "Rules": rules, "HasRegistrySaves": has_registry})
    return catalog


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("upstream", type=Path, help="Path to a clone checked out on the data branch")
    parser.add_argument("--refresh-wikidata", action="store_true", help="Refresh the bundled Wikidata name snapshot")
    parser.add_argument("--report", type=Path, help="Write a JSON report of matching coverage and name conflicts")
    args = parser.parse_args()
    revision = subprocess.check_output(["git", "-C", str(args.upstream), "rev-parse", "HEAD"], text=True).strip()
    with (args.upstream / "data/manifest.yaml").open(encoding="utf-8") as manifest:
        source = yaml.load(manifest, Loader=getattr(yaml, "CSafeLoader", yaml.SafeLoader))
    chinese_aliases = json.loads((ASSETS / "save-catalog-zh.json").read_text(encoding="utf-8"))
    catalog = build_catalog(source, chinese_aliases)
    unmatched = sorted(chinese_aliases.keys() - {entry["Title"] for entry in catalog})
    if unmatched:
        raise SystemExit(f"Aliases missing from the Windows catalog: {', '.join(unmatched)}")
    wikidata = ASSETS / "save-catalog-wikidata.json.gz"
    if args.refresh_wikidata:
        fetch_snapshot(catalog, wikidata)
    report = merge_names(catalog, load_snapshot(wikidata))
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    snapshot = {"Version": 2, "Revision": revision, "Games": catalog}
    content = json.dumps(snapshot, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    (ASSETS / "save-catalog.json.gz").write_bytes(gzip.compress(content, mtime=0))
    print(f"{len(catalog):,} games from {revision}, {report['summary']['chineseSearchableGames']:,} searchable in Chinese")


if __name__ == "__main__":
    main()
