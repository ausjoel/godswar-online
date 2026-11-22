#!/usr/bin/env python3
"""Read the client's NPC function scripts -> data/npc_menu_text.json (the option lines of every NPC function page).

What a player sees when they click an NPC with a function page (teleporter, gear mentor, pet manager ...):
  * the server sends the page number (dialog index = the client's function "Type") and the list of option numbers
    (sub ids, npc_dialogue_profile_entries) - nothing else;
  * the client's Lua (UI/XML/NpcFun/NpcFun.lua -> NpcFunXxx_SetText(Type, Index, BtnID, SubID)) picks the words:
    Button:SetText(NF_L0_4) for an option line, FirstWin_Text1:SetText(...) for the page text;
  * the words themselves are Lua strings, almost all in UI/Base/LuaText.lua (NF_L0_4 = "|cffFFFF00*Go to ...").

So the option text lives in the CLIENT. This script resolves it statically so the editor can show the whole page as
the game draws it. The VPS editor has no client files, so the JSON is built here on the Mac and committed.

  python3 -I build_npc_menu_text.py [CLIENT_DIR]   (default: the newest Development/client-*/Client, else GodsWar.app)

Only NpcFunLoad.xml's scripts are read (that is what the client loads; the big UI/XML/NpcFun.lua is not loaded).
Labels built from run-time values (a local variable, a function call) are kept with {name} placeholders and
"dynamic": true; they are shown but cannot be edited.
"""
import datetime
import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(HERE, "data", "npc_menu_text.json")


def default_client():
    def ver(p):
        m = re.search(r"client-(\d+)\.(\d+)\.(\d+)", p)
        return tuple(int(x) for x in m.groups()) if m else (0,)
    cands = [p for p in glob.glob(os.path.join(ROOT, "Development", "client-*", "Client")) if re.search(r"client-\d+\.\d+\.\d+/Client$", p)]
    if cands:
        return max(cands, key=ver)
    return os.path.join(ROOT, "GodsWar.app", "Contents", "Resources", "Client")


def client_label(client):
    """A short version label only (no local paths in the committed file)."""
    m = re.search(r"client-(\d+\.\d+\.\d+)", client)
    return "client " + m.group(1) if m else "client"


def decode(raw):
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", "replace")
    if raw[:3] == b"\xef\xbb\xbf":
        raw = raw[3:]
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("gb18030", "replace")


# ---------------------------------------------------------------- a small Lua tokenizer (enough for these scripts)
TOKEN = re.compile(r"""
    (?P<lcomment>--\[(?P<eq>=*)\[.*?\](?P=eq)\])
  | (?P<comment>--[^\n]*)
  | (?P<lstr>\[(?P<eq2>=*)\[.*?\](?P=eq2)\])
  | (?P<str>"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*')
  | (?P<num>0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?)
  | (?P<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)
  | (?P<op>\.\.\.|\.\.|==|~=|<=|>=|[-+*/%^#<>=(){}\[\];:,.])
  | (?P<nl>\n)
  | (?P<ws>[ \t\r\f\v]+)
  | (?P<other>.)
""", re.S | re.X)
ESC = {"n": "\n", "t": "\t", "r": "\r", "\\": "\\", '"': '"', "'": "'"}


def unquote(s):
    if s.startswith("["):
        return re.sub(r"^\[=*\[\n?|\]=*\]$", "", s)
    return re.sub(r"\\(.)", lambda m: ESC.get(m.group(1), m.group(1)), s[1:-1])


def tokens(text):
    line = 1
    out = []
    for m in TOKEN.finditer(text):
        kind = m.lastgroup
        if kind in ("eq", "eq2"):
            kind = "lstr" if m.group("lstr") else "lcomment"
        val = m.group(0)
        if kind in ("str", "lstr"):
            out.append(("str", unquote(val), line))
        elif kind in ("num", "name", "op"):
            out.append((kind, val, line))
        line += val.count("\n")
    return out


# ---------------------------------------------------------------- strings: NAME = "..." (later wins, like Lua)
def strings_of(toks, rel, into):
    """Top-level NAME = "literal" assignments (also LuaText.X = ...); table fields [1] = "..." are skipped."""
    for i in range(1, len(toks) - 1):
        if toks[i][1] == "=" and toks[i - 1][0] == "name" and toks[i + 1][0] == "str" and (i + 2 >= len(toks) or toks[i + 2][1] != ".."):
            if i >= 2 and toks[i - 2][1] in (",", "{", "local"):
                continue
            into[toks[i - 1][1]] = {"text": toks[i + 1][1], "file": rel, "line": toks[i + 1][2]}


# ---------------------------------------------------------------- walk the if/elseif tree of the *_SetText functions
def cond_of(toks):
    """{'Index': {1}, 'SubID': {4, 5}, 'Type': {'NPC_FLAG_SYS_TRANMIT'}} from `Index == 1 and (SubID == 4 or SubID==5)`."""
    c = {}
    for i in range(len(toks) - 2):
        a, op, b = toks[i], toks[i + 1], toks[i + 2]
        if op[1] == "==" and a[0] == "name" and a[1] in ("Index", "SubID", "Type", "PreSubID"):
            v = int(b[1]) if b[0] == "num" else b[1]
            c.setdefault(a[1], set()).add(v)
    return c


def expr_parts(toks):
    """A SetText argument: "lit" .. NAME .. "lit" -> [('s', text) | ('k', NAME) | ('v', expr)]."""
    parts, cur = [], []
    depth = 0
    for t in toks + [("op", "..", 0)]:
        if t[1] in ("(", "[", "{"):
            depth += 1
        elif t[1] in (")", "]", "}"):
            depth -= 1
        if t[1] == ".." and depth == 0:
            if len(cur) == 1 and cur[0][0] == "str":
                parts.append(("s", cur[0][1]))
            elif len(cur) == 1 and cur[0][0] == "name":
                parts.append(("k", cur[0][1]))
            elif len(cur) == 4 and cur[0][0] == "name" and cur[1][1] == "[" and cur[2][0] == "str" and cur[3][1] == "]":
                parts.append(("k", cur[0][1] + "." + cur[2][1]))       # LuaText["NF_EM_B15"]
            elif cur:
                parts.append(("v", " ".join(x[1] for x in cur)))
            cur = []
        else:
            cur.append(t)
    return parts


def walk(toks, rel, records, calls):
    """records: SetText calls with the Index / SubID they sit under; calls: dispatcher FUNC(...) calls with their Type."""
    stack = []          # frames: {'kind', 'cond', 'prev'}
    fn = None
    i, n = 0, len(toks)

    def effective(var):
        for f in reversed(stack):
            if f["kind"] != "if":
                continue
            if f["else"] and var in f["prev"]:
                return "else"
            if var in f["cond"]:
                return sorted(f["cond"][var], key=str)
        return None

    while i < n:
        kind, val, line = toks[i]
        if kind == "name" and val == "function":
            j = i + 1
            name = toks[j][1] if j < n and toks[j][0] == "name" else None
            stack.append({"kind": "function", "name": name})
            if not any(f["kind"] == "function" for f in stack[:-1]):
                fn = name
            i += 1
            continue
        if kind == "name" and val in ("if", "elseif"):
            j = i + 1
            while j < n and toks[j][1] != "then":
                j += 1
            c = cond_of(toks[i + 1:j])
            if val == "if":
                stack.append({"kind": "if", "cond": c, "prev": set(c), "else": False})
            elif stack and stack[-1]["kind"] == "if":
                stack[-1]["cond"] = c
                stack[-1]["prev"] |= set(c)
            i = j + 1
            continue
        if kind == "name" and val == "else":
            if stack and stack[-1]["kind"] == "if":
                stack[-1]["cond"] = {}
                stack[-1]["else"] = True
            i += 1
            continue
        if kind == "name" and val in ("do", "repeat"):
            stack.append({"kind": "block"})
            i += 1
            continue
        if kind == "name" and val in ("end", "until"):
            if stack:
                f = stack.pop()
                if f["kind"] == "function" and not any(x["kind"] == "function" for x in stack):
                    fn = None
            i += 1
            continue
        # X:SetText( ... )
        if kind == "name" and i + 3 < n and toks[i + 1][1] == ":" and toks[i + 2][1] == "SetText" and toks[i + 3][1] == "(":
            j, depth = i + 4, 1
            while j < n and depth:
                if toks[j][1] == "(":
                    depth += 1
                elif toks[j][1] == ")":
                    depth -= 1
                j += 1
            records.append({"fn": fn, "target": val, "parts": expr_parts(toks[i + 4:j - 1]), "line": line, "file": rel,
                            "Index": effective("Index"), "SubID": effective("SubID"), "PreSubID": effective("PreSubID")})
            i = j
            continue
        # FUNC_SetText(...) inside the dispatcher
        if kind == "name" and fn == "Set_NpcFun_Text" and val.endswith("_SetText") and i + 1 < n and toks[i + 1][1] == "(":
            calls.append({"fn": val, "Type": effective("Type"), "line": line})
        i += 1


def label(parts, strings):
    text, keys, dynamic = "", [], False
    for kind, v in parts:
        if kind == "s":
            text += v
        elif kind == "k" and v in strings:
            text += strings[v]["text"]
            keys.append(v)
        else:
            text += "{%s}" % v
            dynamic = True
    return {"text": text, "keys": keys, "dynamic": dynamic or len(keys) != 1 or any(k == "s" for k, _ in parts)}


def build(client):
    loc = os.path.join(client, "Localization", "en_us")
    load = os.path.join(loc, "UI", "XML", "NpcFunLoad.xml")
    with open(load, "rb") as f:
        files = re.findall(r'File="\./Localization/en_us/([^"]+)"', decode(f.read()))
    strings, records, calls, consts = {}, [], [], {}
    for rel in files:
        path = os.path.join(loc, rel)
        if not os.path.exists(path):
            continue
        with open(path, "rb") as f:
            text = decode(f.read())
        toks = tokens(text)
        strings_of(toks, rel, strings)
        if rel.endswith("NpcFun/NpcFun.lua"):
            for m in re.finditer(r"^\s*(NPC_FLAG_\w+)\s*=\s*(\d+)", text, re.M):
                consts.setdefault(m.group(1), int(m.group(2)))   # first definition (duplicates are later typos)
        walk(toks, rel, records, calls)
    # Type number -> script function: the dispatcher's if-chain, first matching branch wins
    types = {}
    for c in calls:
        for name in c["Type"] or []:
            v = consts.get(name, name if isinstance(name, int) else None)
            if v is None or str(v) in types:
                continue
            types[str(v)] = {"const": name, "fn": c["fn"]}
    by_fn = {}
    for r in records:
        if r["fn"] and r["fn"].endswith("_SetText"):
            by_fn.setdefault(r["fn"], []).append(r)
    used = set()
    out_types = {}
    for tnum, t in sorted(types.items(), key=lambda kv: int(kv[0])):
        pages = {}
        for r in by_fn.get(t["fn"], []):
            idx = r["Index"]
            idx_keys = ["else"] if idx == "else" else [str(x) for x in (idx or ["*"])]
            sub = r["SubID"]
            sub_keys = ["else"] if sub == "else" else [str(x) for x in (sub or ["*"])]
            lab = label(r["parts"], strings)
            used.update(lab["keys"])
            el = {"target": r["target"], "line": r["line"], "file": r["file"], **lab}
            if r["PreSubID"] not in (None, "else"):
                el["after"] = r["PreSubID"]
            slot = "buttons" if r["target"] == "Button" else ("actions" if r["target"].startswith("FirstWin_ButtonA") else "texts")
            for ik in idx_keys:
                for sk in sub_keys:
                    pages.setdefault(ik, {}).setdefault(sk, {}).setdefault(slot, []).append(el)
        files_of = sorted({r["file"] for r in by_fn.get(t["fn"], [])})
        out_types[tnum] = {"const": t["const"], "fn": t["fn"], "file": files_of[0] if files_of else None, "pages": pages}
    return {"generated": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "client": client_label(client), "scripts": len(files), "types": out_types,
            "strings": {k: strings[k] for k in sorted(used) if k in strings}}


def main():
    client = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else default_client()
    doc = build(client)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
        f.write("\n")
    print("%s: %d function pages, %d strings from %s" % (os.path.relpath(OUT, HERE), len(doc["types"]), len(doc["strings"]), doc["client"]))


if __name__ == "__main__":
    main()
