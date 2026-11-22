#!/usr/bin/env python3
"""Apply the editor's NPC option-text draft to a client tree (for the next client build). B21 item 20.

  python3 -I tools/apply_npc_menu_text.py DRAFT.json CLIENT_DIR [--dry-run]

DRAFT.json = the file the editor's "Download client patch" gives (or the VPS editor's backups/npc-menu-text-draft.json):
{"labels": {"NF_L0_6": "|cffFFFF00*Go to Marathon|cFFFFFFFF", ...}}.
Each text is replaced IN PLACE where the client defines it (data/npc_menu_text.json strings[KEY].file / line), so a
later script cannot override it. The defining line must still hold the original text, else the key is refused
(the client changed since the data was built: run build_npc_menu_text.py again). A .orig copy is kept beside every
changed file. Files keep their encoding (UTF-8, with BOM if it had one) and line endings.
"""
import json
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(HERE, "data", "npc_menu_text.json")


def lua_quote(text):
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def plan(labels, strings, client):
    """-> {path: [(line_no, key, old_line, new_line)]}, errors"""
    out, errors = {}, []
    loc = os.path.join(client, "Localization", "en_us")
    for key, text in sorted(labels.items()):
        info = strings.get(key)
        if not info:
            errors.append("%s: not a known client text" % key)
            continue
        if not isinstance(text, str) or not text.strip() or "\n" in text or "\r" in text or "\x00" in text:
            errors.append("%s: the new text must be one non-empty line" % key)
            continue
        out.setdefault(os.path.join(loc, info["file"]), []).append((info["line"], key, info["text"], text))
    return out, errors


def apply_file(path, edits, dry):
    raw = open(path, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw[3:].decode("utf-8") if bom else raw.decode("utf-8")
    lines = text.split("\n")
    done, errors = [], []
    for line_no, key, old, new in edits:
        i = line_no - 1
        if i >= len(lines):
            errors.append("%s: line %d is past the end of %s" % (key, line_no, path))
            continue
        rx = re.compile(r"(?<![\w.])(" + re.escape(key) + r"\s*=\s*)" + re.escape(lua_quote(old)))
        m = rx.search(lines[i])
        if not m:
            # the source may have used other escapes; accept any literal whose value equals the old text
            m2 = re.search(r"(?<![\w.])(" + re.escape(key) + r"\s*=\s*)(\"(?:[^\"\\]|\\.)*\")", lines[i])
            if not m2 or re.sub(r"\\(.)", r"\1", m2.group(2)[1:-1]) != old:
                errors.append("%s: %s line %d no longer holds the original text" % (key, os.path.basename(path), line_no))
                continue
            lines[i] = lines[i][:m2.start(2)] + lua_quote(new) + lines[i][m2.end(2):]
        else:
            lines[i] = lines[i][:m.start()] + m.group(1) + lua_quote(new) + lines[i][m.end():]
        done.append(key)
    if done and not errors and not dry:
        if not os.path.exists(path + ".orig"):
            shutil.copy2(path, path + ".orig")
        body = "\n".join(lines).encode("utf-8")
        with open(path, "wb") as f:
            f.write((b"\xef\xbb\xbf" if bom else b"") + body)
    return done, errors


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    draft = json.load(open(argv[1], encoding="utf-8"))
    client = os.path.abspath(argv[2])
    dry = "--dry-run" in argv
    strings = json.load(open(DATA, encoding="utf-8"))["strings"]
    files, errors = plan(draft.get("labels") or {}, strings, client)
    changed = []
    for path, edits in files.items():
        done, errs = apply_file(path, edits, dry)
        errors += errs
        changed += done
    for e in errors:
        print("REFUSED", e)
    print("%s %d option text(s)%s" % ("would change" if dry else "changed", len(changed), "" if not errors else "; nothing written for files with errors"))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
