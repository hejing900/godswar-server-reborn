"""Resolve item name keys to item ids from the client's ItemBaseAttribute.xml.

This is the client's own id source: every row is `<Key ID="nnn" Type="..."/>`,
and the ids match the server's `item_templates` table exactly (checked against
`Kaddish`=3900, `MaterialOdds1`=4230, `Rmacadam1`=9990, `Butalcohol`=4265). The
locale only changes the display text, never the id.

Usage:
    python tools/find_item_ids.py Kaddish MaterialOdds1
    python tools/find_item_ids.py --keys-file artifacts/tmp/event-item-keys.txt
"""
import argparse
import re
from pathlib import Path

CLIENT_ROOT = Path(r"D:\Godswar Origin\Localization")
ROW = re.compile(r'<([A-Za-z_][\w]*)\s+ID="(\d+)"\s+Type="([^"]*)"')


def load(locale):
    path = CLIENT_ROOT / locale / "Settings" / "Sys" / "ItemBaseAttribute.xml"
    raw = path.read_bytes()
    text = raw.decode("utf-16" if raw[:2] in (b"\xff\xfe", b"\xfe\xff") else "utf-8-sig",
                      "replace")
    rows = {}
    for key, item_id, kind in ROW.findall(text):
        rows.setdefault(key, (int(item_id), kind))
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("keys", nargs="*")
    parser.add_argument("--locale", default="en_us")
    parser.add_argument("--keys-file")
    parser.add_argument("--search", help="substring filter over the key names")
    args = parser.parse_args()

    keys = list(args.keys)
    if args.keys_file:
        keys += [
            line.strip()
            for line in Path(args.keys_file).read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]

    rows = load(args.locale)
    print(f"locale={args.locale} keys={len(rows)}")
    if args.search:
        for key, (item_id, kind) in sorted(rows.items(), key=lambda kv: kv[1][0]):
            if args.search in key:
                print(f"  {item_id}\t{key}\t{kind}")
        return 0

    for key in keys:
        hit = rows.get(key)
        print(f"  {hit[0] if hit else '-':>6}\t{key}\t{hit[1] if hit else 'NOT FOUND'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
