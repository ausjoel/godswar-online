#!/usr/bin/env python3
"""Count the |cAARRGGBB colour codes the client's English text actually uses -> data/colour_codes.json.

The NPC greeting editor's colour toolbar lists these (most used first) so a greeting keeps to the game's own
palette. The VPS editor has no client files, so the counts are built here on the Mac and committed.

  python3 -I build_colour_codes.py [CLIENT_DIR]     (default: the top-level GodsWar.app client)

Text files are UTF-16 (BOM) key<TAB>value, UTF-8 or GB18030; all three are read. Codes are counted
case-insensitively (|cFF39d8b8 == |cFF39D8B8) and reported upper-case.
"""
import collections
import datetime
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CLIENT = os.path.abspath(os.path.join(HERE, "..", "..", "GodsWar.app", "Contents", "Resources", "Client"))
OUT = os.path.join(HERE, "data", "colour_codes.json")
CODE = re.compile(r"\|c([0-9A-Fa-f]{8})")
# Names for the common ones (the game has none; these are descriptive labels only).
NAMES = {"FFFFFFFF": "white (default text)", "FF39D8B8": "teal (NPC names, highlights)", "FFD7DD80": "pale yellow (item stats)",
         "FFFFF334": "yellow", "FFF14187": "pink", "FFFFFF00": "bright yellow", "FFFAA955": "orange", "FFDCC883": "gold (effects)",
         "FFE3170D": "red", "FFF89E19": "amber", "FF00FF00": "green", "FFFF0000": "pure red", "FFF79709": "dark orange",
         "FFFFBBFF": "lilac", "FFF0C800": "gold yellow", "FFE4DBED": "pale grey", "FFD5912B": "bronze", "FF08D631": "bright green",
         "FFCD7F32": "copper"}


def decode(raw):
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", "replace")
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("gb18030", "replace")


def scan(loc):
    total, text_only, files, sample = collections.Counter(), collections.Counter(), collections.defaultdict(set), {}
    for root, _dirs, names in os.walk(loc):
        for name in sorted(names):
            path = os.path.join(root, name)
            rel = os.path.relpath(path, loc)
            with open(path, "rb") as f:
                body = decode(f.read())
            for m in CODE.finditer(body):
                code = m.group(1).upper()
                total[code] += 1
                if rel.startswith("Text" + os.sep):
                    text_only[code] += 1
                files[code].add(re.sub(r"^(Text/Quest)/.*", r"\1/*.dat", rel.replace(os.sep, "/")))
                is_text = rel.startswith("Text" + os.sep)
                if code not in sample or (is_text and not sample[code][0]):
                    tail = re.split(r"\|c[0-9A-Fa-f]{8}|\|r|[\r\n\t]", body[m.end():m.end() + 80])[0].strip()
                    if tail:
                        sample[code] = (is_text, tail[:40])
    return total, text_only, files, sample


def main(argv):
    client = os.path.abspath(argv[1]) if len(argv) > 1 else DEFAULT_CLIENT
    loc = os.path.join(client, "Localization", "en_us")
    if not os.path.isdir(loc):
        sys.exit("no %s" % loc)
    total, text_only, files, sample = scan(loc)
    rows = [{"code": c, "rgb": "#" + c[2:], "count": n, "text_count": text_only.get(c, 0), "name": NAMES.get(c, ""),
             "sample": sample.get(c, (0, ""))[1], "files": sorted(files[c])[:6]} for c, n in total.most_common()]
    doc = {"generated": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
           "source": "Localization/en_us (Text/*.dat, UI/*.lua, Settings) of the GodsWar client",
           "format": "|cAARRGGBB starts a colour (alpha is ignored by the game); the next |c or |r ends it",
           "total_uses": sum(total.values()), "colours": rows}
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
    print("%d colours, %d uses -> %s" % (len(rows), doc["total_uses"], OUT))


if __name__ == "__main__":
    main(sys.argv)
