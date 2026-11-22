#!/usr/bin/env python3
"""Writes data/gm_powers_inventory.json from the game server source (Application/Operators/GmPowerPolicy.cs): the
editor's read-only copy of the GM power catalogue, shown by the GM powers page while the running game build does not
describe its powers itself. tests/test_gm_powers_unit.py fails when the copy and the source differ.
Run: python3 -I build_gm_powers_inventory.py [--check]"""
import json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "..", "server", "src", "Godswar.Server", "Application", "Operators", "GmPowerPolicy.cs")
OUT = os.path.join(HERE, "data", "gm_powers_inventory.json")
STRING = re.compile(r'"((?:[^"\\]|\\.)*)"')


def parse(text):
    consts = dict(re.findall(r'public const string (\w+) = "([^"]*)";', text))
    body = text[text.index("Definitions { get; } ="):]
    body = body[:body.index("];")]
    out = []
    for block in re.findall(r"new\((.*?)\)(?:,|\s*$)", body, re.S | re.M):
        args, depth, cur, in_str, i = [], 0, "", False, 0
        while i < len(block):                       # split on top-level commas, outside string literals
            ch = block[i]
            if in_str:
                cur += ch
                if ch == "\\":
                    cur += block[i + 1]; i += 1
                elif ch == '"':
                    in_str = False
            elif ch == '"':
                in_str = True; cur += ch
            elif ch == "," and depth == 0:
                args.append(cur); cur = ""
            else:
                cur += ch
            i += 1
        args.append(cur)
        vals = []
        for a in args:
            lits = STRING.findall(a)
            vals.append("".join(l.replace('\\"', '"') for l in lits) if lits else consts.get(a.strip(), a.strip()))
        if len(vals) < 4:
            raise SystemExit("cannot parse a power definition: %r" % block[:80])
        out.append({"key": vals[0], "group": vals[1], "label": vals[2], "description": vals[3], "risk": vals[4] if len(vals) > 4 else "normal"})
    return out


def main():
    powers = parse(open(SOURCE, encoding="utf-8").read())
    doc = {"source": "server/src/Godswar.Server/Application/Operators/GmPowerPolicy.cs", "powers": powers}
    text = json.dumps(doc, indent=1, ensure_ascii=False) + "\n"
    if "--check" in sys.argv:
        same = os.path.exists(OUT) and open(OUT, encoding="utf-8").read() == text
        print("gm_powers_inventory.json %s (%d powers)" % ("is current" if same else "IS STALE: run build_gm_powers_inventory.py", len(powers)))
        return 0 if same else 1
    with open(OUT, "w", encoding="utf-8") as f:
        f.write(text)
    print("wrote %s (%d powers)" % (OUT, len(powers)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
