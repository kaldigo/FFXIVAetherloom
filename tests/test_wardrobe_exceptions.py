import csv
import json
from pathlib import Path
import tempfile
import unittest

from tools.wardrobe_exceptions import generate
from tools.refresh_wardrobe_exceptions import store_items


class WardrobeExceptionTests(unittest.TestCase):
    def test_collect_ids_are_mapped_to_game_items(self):
        cabinet = [{'#': '1022', 'Item': '52414', 'Category': '10', 'Order': '50857'}]
        collection = {'results': [dict(id=1023, name='Alternative Ruffled Dress',
            category={'id': 10}, order=50857, sources=[dict(type='Premium', text='Online Store')])]}
        self.assertEqual(store_items(cabinet, collection), {'52414': 'Alternative Ruffled Dress'})
        with self.assertRaises(ValueError):
            store_items([], collection)

    def test_normal_sources_win_and_conditional_reclaims_preserve_exemptions(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for sheet, columns, records in [
                ('Cabinet', ['Category', 'Item'], [[2, 100], [2, 101]]),
                ('JournalGenre', ['#', 'JournalCategory'], [[237, 97]]),
                ('Quest', ['#', 'Festival', 'JournalGenre'], [[1, 0, 237], [2, 0, 5]]),
                ('AchievementCategory', ['#', 'Name'], [[38, 'Seasonal Events']]),
                ('Achievement', ['#', 'AchievementCategory'], []),
                ('SpecialShop', ['#', 'RequiredFestival', 'Quest'], []),
            ]:
                with (root / (sheet + '.csv')).open('w', newline='') as out:
                    writer = csv.writer(out)
                    writer.writerow(columns)
                    writer.writerows(records)

            def source(kind, data):
                return dict(type=kind, data=data)

            store = source(21, {})
            rows = {
                100: [source(18, [1])],
                101: [source(18, [1, 2])],
                102: [store],
                103: [store, source(6, [1])],
                104: [store, source(3, [dict(npcId=1017613, festival=0)])],
                105: [store, source(3, [dict(npcId=123, festival=0)])],
                106: [source(1, [1])],
                108: [source(13, [102])],  # Store-only bundle contents
                109: [store, source(13, [102, 106])],  # Also in a gameplay coffer
                110: [source(13, [108])],  # Nested bundle
                111: [store, source(13, [999])],  # Unknown container requires ownership
            }
            extracts = {i: dict(id=i, sources=s) for i, s in rows.items()}
            (root / 'extracts.json').write_text(json.dumps(extracts))
            self.assertEqual(generate(root, supplemental=[]), [100, 102, 104, 108, 110])
            releases = [dict(items={'105': 'Normal vendor overlap', '107': 'New store release'})]
            self.assertEqual(generate(root, supplemental=releases), [100, 102, 104, 107, 108, 110])

    def test_shipped_catalog_keeps_distinct_event_and_gameplay_items(self):
        catalog = Path(__file__).resolve().parents[1] / 'Aetherloom/WardrobeExceptions.json'
        items = json.loads(catalog.read_text())
        self.assertIn(13331, items)  # Maiden's Rhapsody
        self.assertIn(6976, items)   # Valentione gear, also sold on the store
        for item in [36335, *range(20628, 20633)]:  # Blackbosom store bundle weapons
            self.assertIn(item, items)
        for item in range(52413, 52418):  # Alternative Dress Attire
            self.assertIn(item, items)
        self.assertNotIn(16068, items)  # GARO weapon available with Wolf Marks
        self.assertNotIn(45057, items)  # Figmental weapon from treasure content
