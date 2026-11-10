"""Bounded session-end metadata reader. Never return raw log lines or packet contents."""
import re

MAX_BYTES = 2_000_000
NUMBERS = ("utc-seconds", "character-id", "account-id", "duration-ms", "last-opcode", "last-send-opcode", "last-send-bytes")
BOOLEANS = ("character-loaded", "client-ready", "ui-ready", "bootstrap")
CODES = ("reason", "error", "transport-error")

def recent_sessions(path, cid, limit=20):
    if not path:
        return []
    try:
        with open(path, "rb") as stream:
            stream.seek(0, 2)
            stream.seek(max(0, stream.tell() - MAX_BYTES))
            lines = stream.read(MAX_BYTES).decode("utf-8", "replace").splitlines()
    except OSError:
        return []
    rows = []
    for line in reversed(lines):
        if not line.startswith("[game-session-end] ") or len(line) > 1400:
            continue
        tokens = line.split("[game-session-end] ", 1)[1].split()
        fields = {}
        for token in tokens:
            k, sep, v = token.partition("=")
            if sep and k not in fields:
                fields[k] = v
        if fields.get("character-id") != str(int(cid)):
            continue
        if not all(re.fullmatch(r"[0-9]{1,18}", fields.get(k, "")) for k in NUMBERS):
            continue
        if not all(fields.get(k) in ("True", "False") for k in BOOLEANS):
            continue
        if not all(re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,79}", fields.get(k, "")) for k in CODES):
            continue
        row = {k: int(fields[k]) for k in NUMBERS}
        row.update({k: fields[k] == "True" for k in BOOLEANS})
        row.update({k: fields[k] for k in CODES})
        row["ended_before_ready"] = not row["bootstrap"]
        rows.append(row)
        if len(rows) >= min(20, max(1, limit)):
            break
    return rows
