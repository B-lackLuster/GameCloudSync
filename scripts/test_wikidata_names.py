import unittest

from wikidata_names import merge_names


def game(title, *app_ids, chinese_names=()):
    return {"Title": title, "SteamIds": list(app_ids), "ChineseNames": list(chinese_names), "Aliases": []}


def item(qid, apps, *names):
    return {"id": qid, "apps": apps, "names": names}


class WikidataNamesTests(unittest.TestCase):
    def test_merges_by_id_and_preserves_the_curated_display_name(self):
        first = game("Localized Game", 100, chinese_names=["星际游戏", "星球"])
        second = game("Another Game", 200)
        items = [
            item("Q1", [100], ("zh-tw", "label", "星際遊戲"), ("zh-cn", "label", "星空遊戲")),
            item("Q2", [200], ("zh", "alias", "小世界")),
            item("Q3", [999], ("zh", "label", "无关游戏")),
        ]
        report = merge_names([first, second], items)
        self.assertEqual(first["ChineseTitle"], "星际游戏")
        self.assertEqual(first["ChineseNames"], ["星际游戏", "星空遊戲", "星際遊戲"])
        self.assertEqual(first["Aliases"], ["星球"])
        self.assertIsNone(second["ChineseTitle"])
        self.assertEqual(second["Aliases"], ["小世界"])
        self.assertEqual(report["summary"]["wikidataMatches"], 2)

    def test_id_and_edition_collisions_do_not_import_names(self):
        catalog = [game("Base", 10), game("Release", 20), game("Expansion", 20), game("Remaster", 30)]
        items = [
            item("Q1", [10, 30], ("zh", "label", "系列名称")),
            item("Q2", [20], ("zh", "label", "版本名称")),
            item("Q3", [30]),
        ]
        report = merge_names(catalog, items)
        self.assertEqual([20, 30], [row["appId"] for row in report["appIdConflicts"]])
        self.assertEqual(["Q1", "Q2"], [row["item"] for row in report["editionConflicts"]])
        self.assertTrue(all(entry["ChineseTitle"] is None for entry in catalog))

    def test_same_visible_name_stays_searchable_for_both_games_and_is_reported(self):
        first = game("Base", 10)
        second = game("Remaster", 11)
        report = merge_names([first, second], [
            item("Q1", [10], ("zh", "label", "游戏名")),
            item("Q2", [11], ("zh", "label", "游戏 名")),
        ])
        self.assertEqual(report["nameConflicts"], [
            {"names": ["游戏 名", "游戏名"], "games": ["Base", "Remaster"]},
        ])
        self.assertEqual(first["ChineseTitle"], "游戏名")
        self.assertEqual(second["ChineseTitle"], "游戏 名")


if __name__ == "__main__":
    unittest.main()
