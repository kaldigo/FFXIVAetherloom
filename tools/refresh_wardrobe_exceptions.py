"""Refresh bundled exceptions from current Teamcraft, game sheets and FFXIV Collect.

Collect records use their own IDs. Match their category and display order to Cabinet,
then use Cabinet.Item as the game item ID. The normal acquisition checks in generate
still take precedence over every store source.
"""
import csv
import json
import os
from pathlib import Path
import tempfile
from urllib.request import Request, urlopen

from tools.wardrobe_exceptions import generate


def download(url):
    headers = {'User-Agent': 'Aetherloom-wardrobe-catalog', 'Accept': 'application/json'}
    if url.startswith('https://api.github.com/') and os.environ.get('GH_TOKEN'):
        headers['Authorization'] = 'Bearer ' + os.environ['GH_TOKEN']
    with urlopen(Request(url, headers=headers), timeout=120) as response:
        return response.read()


def store_items(cabinet, collection):
    lookup = {}
    for row in cabinet:
        key = (int(row['Category']), int(row['Order']))
        lookup.setdefault(key, set()).add(int(row['Item']))
    items = {}
    for row in collection['results']:
        if not any(s['type'] == 'Premium' and s['text'] == 'Online Store' for s in row['sources']):
            continue
        ids = lookup.get((row['category']['id'], row['order']), set()) - {0}
        if len(ids) != 1:
            raise ValueError(f"No unique Cabinet item for store release {row['name']}")
        items[str(next(iter(ids)))] = row['name']
    if not items:
        raise ValueError('The current store collection was empty.')
    return items


def refresh():
    root = Path(__file__).resolve().parents[1]
    with tempfile.TemporaryDirectory() as folder:
        directory = Path(folder)
        game_commit = json.loads(download('https://api.github.com/repos/xivapi/ffxiv-datamining/commits/master'))['sha']
        extract_commit = json.loads(download('https://api.github.com/repos/ffxiv-teamcraft/ffxiv-teamcraft/commits?path=libs/data/src/lib/extracts/extracts.json&per_page=1'))[0]['sha']
        for name in ('Cabinet', 'Quest', 'JournalGenre', 'Achievement', 'AchievementCategory', 'SpecialShop'):
            (directory / (name + '.csv')).write_bytes(download(f'https://raw.githubusercontent.com/xivapi/ffxiv-datamining/{game_commit}/csv/en/{name}.csv'))
        (directory / 'extracts.json').write_bytes(download(f'https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/{extract_commit}/libs/data/src/lib/extracts/extracts.json'))
        collection = json.loads(download('https://ffxivcollect.com/api/armoires'))
        with (directory / 'Cabinet.csv').open(encoding='utf-8-sig', newline='') as file:
            items = store_items(csv.DictReader(file), collection)
        releases = json.loads((root / 'tools/wardrobe_store_releases.json').read_text(encoding='utf-8'))
        releases.append({'items': items})
        result = generate(directory, supplemental=releases)
        if not result:
            raise ValueError('Refusing to publish an empty exemption catalog.')
        (root / 'Aetherloom/WardrobeExceptions.json').write_text(json.dumps(result) + '\n', encoding='utf-8')
        print(f'Generated {len(result)} exceptions; game {game_commit}, extracts {extract_commit}, {len(items)} current store items.')


if __name__ == '__main__':
    refresh()
