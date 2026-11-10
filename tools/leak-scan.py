#!/usr/bin/env python3
"""Fail if personal details are about to be committed.

    python3 -I tools/leak-scan.py

Generic checks (home paths, private keys, e-mail addresses) always run. Your own details
(your server IP, domain, name, account names) go one per line in local/leak-patterns.txt,
which is git-ignored, so they are checked without ever being written into the project.
Lines are regular expressions; blank lines and lines starting with # are ignored.
"""
import re, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GENERIC = {
    "home path": rb"/(?:Users|home)/[A-Za-z][\w.-]*/",
    "private key": rb"-----BEGIN [A-Z ]*PRIVATE KEY",
    "e-mail": rb"[A-Za-z0-9._%+-]+@(?!(?:example|invalid|localhost|your|ex|b|y|YOUR-DOMAIN)\.)[A-Za-z0-9.-]+\.[a-z]{2,}",
}
SKIP = {".git", "bin", "obj", "node_modules", "__pycache__", "local"}

def tracked_or_all():
    try:
        out = subprocess.run(["git", "ls-files", "-co", "--exclude-standard"], cwd=ROOT, capture_output=True, check=True).stdout
        return [ROOT / p for p in out.decode().split("\n") if p]
    except Exception:
        return [p for p in ROOT.rglob("*") if p.is_file() and not set(p.relative_to(ROOT).parts) & SKIP]

def main():
    pats = dict(GENERIC)
    lp = ROOT / "local/leak-patterns.txt"
    if lp.exists():
        for i, line in enumerate(lp.read_text().splitlines()):
            if line.strip() and not line.startswith("#"):
                pats[f"yours:{i+1}"] = line.strip().encode()
    bad = 0
    for f in tracked_or_all():
        if not f.is_file() or f.name == "leak-scan.py" or f.suffix in {".png", ".jpg", ".ico", ".gz", ".zip"}:
            continue
        data = f.read_bytes()
        for name, pat in pats.items():
            if name == "e-mail" and any(x in f.parts for x in ("tests", "test")):
                continue
            m = re.search(pat, data)
            if m:
                bad += 1
                print(f"LEAK [{name}] {f.relative_to(ROOT)}: {data[max(0,m.start()-30):m.end()+30].decode('utf-8','replace')!r}")
    print("clean" if not bad else f"{bad} problem(s)")
    sys.exit(1 if bad else 0)

main()
