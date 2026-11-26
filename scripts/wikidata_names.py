"""Download and validate the Wikidata names used by the offline save catalog."""

from collections import defaultdict
from email.utils import parsedate_to_datetime
import gzip
import json
from pathlib import Path
import re
import time
import unicodedata
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


ENDPOINT = "https://query.wikidata.org/sparql"
IDENTITY = "GameCloudSync-Catalog/1.0 (https://github.com/B-lackLuster/GameCloudSync/issues)"
LANGUAGES = ("zh-cn", "zh-hans", "zh", "zh-sg", "zh-my", "zh-tw", "zh-hant", "zh-hk", "zh-mo")
# Drop encyclopedia disambiguation only. Edition names and sequel numbers stay searchable.
DISAMBIGUATOR = re.compile(r"\s*[(（](?:电子游戏|電子遊戲|电子游戏作品|電子遊戲作品|電玩遊戲|视频游戏|視頻遊戲|遊戲)[)）]$")
HAN = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\U00020000-\U000323af]")
QUERY = """SELECT DISTINCT ?appId ?item ?kind ?name WHERE {
  { SELECT DISTINCT ?appId ?item WHERE {
      VALUES ?appId { @IDS@ }
      { ?item wdt:P1733 ?appId }
      UNION { ?statement pq:P1733 ?appId . ?item p:P400 ?statement .
              FILTER NOT EXISTS { ?statement wikibase:rank wikibase:DeprecatedRank } }
      UNION { ?statement pq:P1733 ?appId . ?item p:P407 ?statement .
              FILTER NOT EXISTS { ?statement wikibase:rank wikibase:DeprecatedRank } }
  } }
  OPTIONAL {
    VALUES ?kind { rdfs:label skos:altLabel }
    ?item ?kind ?name .
    FILTER(LANGMATCHES(LANG(?name), "zh"))
  }
}"""


def search_key(value):
    return "".join(ch for ch in unicodedata.normalize("NFKC", value).upper()
                   if unicodedata.category(ch).startswith("L") or unicodedata.category(ch) == "Nd")


def destinations(catalog):
    games = defaultdict(set)
    for game in catalog:
        for app_id in game["SteamIds"]:
            games[app_id].add(game["Title"])
    return games


def request_rows(query):
    request = Request(f"{ENDPOINT}?{urlencode({'query': query, 'format': 'json'})}", headers={
        "User-Agent": IDENTITY,
        "Accept": "application/sparql-results+json",
        "Accept-Encoding": "gzip",
    })
    for attempt in range(3):
        try:
            with urlopen(request, timeout=32) as response:
                content = response.read()
                if response.headers.get("Content-Encoding") == "gzip":
                    content = gzip.decompress(content)
                return json.loads(content)["results"]["bindings"]
        except HTTPError as error:
            if error.code not in (429, 502, 503, 504) or attempt == 2:
                raise
            delay = 3 * (attempt + 1)
            if retry_after := error.headers.get("Retry-After"):
                delay = max(delay, int(retry_after) if retry_after.isdigit()
                            else parsedate_to_datetime(retry_after).timestamp() - time.time())
            if delay > 120:
                raise
            print(f"Wikidata returned {error.code}. Waiting {delay:.0f} seconds.", flush=True)
            time.sleep(delay)
        except (URLError, TimeoutError):
            if attempt == 2:
                raise
            time.sleep(3 * (attempt + 1))


def fetch_snapshot(catalog, snapshot_path: Path):
    app_ids = sorted(destinations(catalog))
    size = 350
    batches = (len(app_ids) + size - 1) // size
    records = {}
    for offset in range(0, len(app_ids), size):
        print(f"Wikidata batch {offset // size + 1}/{batches}", flush=True)
        batch = app_ids[offset:offset + size]
        query = QUERY.replace("@IDS@", " ".join(json.dumps(str(app_id)) for app_id in batch))
        for row in request_rows(query):
            item = row["item"]["value"].rsplit("/", 1)[-1]
            app_id = int(row["appId"]["value"])
            if not re.fullmatch(r"Q[1-9]\d*", item) or app_id not in batch:
                raise ValueError("Unexpected identifier in Wikidata response")
            name = row.get("name", {})
            record = records.setdefault(item, {"id": item, "apps": set(), "names": set()})
            record["apps"].add(app_id)
            lang = name.get("xml:lang")
            if lang in LANGUAGES:
                title = DISAMBIGUATOR.sub("", name["value"].strip()).strip()
                if HAN.search(title) and len(title) <= 200:
                    kind = "label" if row["kind"]["value"].endswith("#label") else "alias"
                    record["names"].add((lang, kind, title))
        if offset + size < len(app_ids):
            time.sleep(0.3)
    if not any(record["names"] for record in records.values()):
        raise ValueError("No Chinese names returned; keeping the existing snapshot")
    items = [{"id": item["id"], "apps": sorted(item["apps"]), "names": sorted(item["names"])}
             for item in sorted(records.values(), key=lambda record: int(record["id"][1:]))]
    payload = json.dumps({"version": 1, "items": items}, ensure_ascii=False, separators=(",", ":"))
    staging = snapshot_path.with_suffix(".tmp")
    staging.write_bytes(gzip.compress(payload.encode("utf-8"), mtime=0))
    staging.replace(snapshot_path)


def load_snapshot(path: Path):
    with gzip.open(path, "rt", encoding="utf-8") as stream:
        snapshot = json.load(stream)
    if snapshot["version"] != 1:
        raise ValueError("Unsupported Wikidata name snapshot version")
    return snapshot["items"]


def merge_names(catalog, items):
    games_for_app = destinations(catalog)
    items_for_app = defaultdict(set)
    for item in items:
        for app_id in item["apps"]:
            if app_id in games_for_app:
                items_for_app[app_id].add(item["id"])

    report = {"appIdConflicts": [], "editionConflicts": [], "nameConflicts": []}
    for app_id in sorted(games_for_app.keys() & items_for_app.keys()):
        if len(games_for_app[app_id]) > 1 or len(items_for_app[app_id]) > 1:
            report["appIdConflicts"].append({"appId": app_id, "games": sorted(games_for_app[app_id]),
                                             "items": sorted(items_for_app[app_id])})
    ambiguous = {row["appId"] for row in report["appIdConflicts"]}

    imported = defaultdict(list)
    for item in items:
        app_ids = [app_id for app_id in item["apps"] if app_id in games_for_app]
        titles = set().union(*(games_for_app[app_id] for app_id in app_ids))
        if len(titles) > 1:
            report["editionConflicts"].append({"item": item["id"], "appIds": app_ids, "games": sorted(titles)})
        # An item may describe a family of editions. A unique alias must resolve to one game and one item.
        if len(titles) == 1 and not any(app_id in ambiguous for app_id in app_ids) and item["names"]:
            imported[next(iter(titles))].extend(item["names"])

    names_to_games = defaultdict(set)
    labels_by_key = defaultdict(set)
    covered = 0
    searchable = 0
    for game in catalog:
        imported_names = sorted(imported[game["Title"]], key=lambda name: (LANGUAGES.index(name[0]), name[2]))
        labels = [title for _, kind, title in imported_names if kind == "label"]
        aliases = [title for _, kind, title in imported_names if kind == "alias"]
        if imported_names:
            covered += 1
        manual = game["ChineseNames"]
        if manual or imported_names:
            searchable += 1
        game["ChineseTitle"] = manual[0] if manual else (labels[0] if labels else None)
        game["ChineseNames"] = list(dict.fromkeys([*manual[:1], *labels]))
        game["Aliases"] = list(dict.fromkeys([*game["Aliases"], *manual[1:], *aliases]))
        for name in set(game["ChineseNames"] + game["Aliases"] + [game["Title"]]):
            if HAN.search(name):
                key = search_key(name)
                if key:
                    names_to_games[key].add(game["Title"])
                    labels_by_key[key].add(name)

    report["nameConflicts"] = [{"names": sorted(labels_by_key[key]), "games": sorted(titles)}
                               for key, titles in sorted(names_to_games.items()) if len(titles) > 1]
    report["summary"] = {"catalogGames": len(catalog), "wikidataMatches": covered,
                         "chineseSearchableGames": searchable}
    return report
