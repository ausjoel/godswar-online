#!/usr/bin/env python3
"""Writes the launcher's "What's new" items (JSON) from the player site's news list.

Usage: python3 -I news_from_site.py _news.html > news.json
Input: signup/templates/_news.html (a Jinja `{% set items = [dict(date=..., title=..., text=..., link=...), ...] %}`),
newest first. Only plain string constants are read (ast, nothing is executed). Prints [] and a warning on any problem,
so a news hiccup never stops a publish.
"""
import ast, hashlib, json, re, sys

SITE = "https://YOUR-DOMAIN.example"


def shorten(s, n=300):
    s = " ".join(s.split())
    if len(s) <= n:
        return s
    cut = s[:n]
    if " " in cut[n // 2:]:
        cut = cut[:cut.rfind(" ")]
    return cut.rstrip(" ,;:.") + "..."


def items(text):
    m = re.search(r"\{%-?\s*set\s+items\s*=\s*(\[.*?\])\s*-?%\}", text, re.S)
    if not m:
        raise ValueError("no `set items = [...]` block")
    out = []
    for call in ast.parse(m.group(1), mode="eval").body.elts:
        if not (isinstance(call, ast.Call) and getattr(call.func, "id", "") == "dict"):
            raise ValueError("unexpected entry")
        kw = {k.arg: k.value.value for k in call.keywords
              if isinstance(k.value, ast.Constant) and isinstance(k.value.value, str)}
        date, title = kw.get("date", "").strip(), kw.get("title", "").strip()
        if not date or not title:
            continue
        out.append({
            "id": hashlib.sha1(f"{date}|{title}".encode()).hexdigest()[:12],
            "date": date,
            "title": title,
            "url": SITE + "/news",
            "summary": shorten(kw.get("text", "")),
        })
        if len(out) == 3:
            break
    return out


if __name__ == "__main__":
    try:
        res = items(open(sys.argv[1], encoding="utf-8").read())
    except Exception as e:  # never fail the publish over news
        print(f"news_from_site: {e}; publishing no news", file=sys.stderr)
        res = []
    json.dump(res, sys.stdout, ensure_ascii=False, indent=1)
    print()
