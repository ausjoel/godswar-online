"""modules/graphics.py - Graphics: 3D models, map pictures, every client texture and the skill effects.

Read-only on the game client and the database. The one POST (effect/save) writes an edited COPY of an effect file
into the graphics output folder, never into the client.

Where the data comes from (all optional; each tab says what is missing):

  static/graphics/          (in git) thumbnail sheets, models.json, maps.json, map underlays, icons-extra.*
  GW_GRAPHICS_OUT           (not in git) models/*.glb, stills/*.png, maps/*-minimap.png / *-terrain.png made by
                            tools/graphics/build_*.py; default <project>/Development/w4/graphics-out
  GW_CLIENT                 the client folder, for the texture browser, effects and on-demand model conversion;
                            default <project>/GodsWar.app/Contents/Resources/Client
  GW_CLIENT_MODS            gwx.py / gwo.py / gwmap.py (format readers); default <project>/GodsWar-2.51/Client-Mods

  GW_GRAPHICS_OUT/web       (not in git) the PREBUILT copy of everything the Textures and Effects tabs read, made on
                            the Mac by tools/graphics/build_web.py: every client picture as WebP, the folder index,
                            UI / item-icon layout, every effect as .glb + its element table + the original .gwm.
                            With it a server needs neither the client, the format readers nor Pillow.
  GW_GRAPHICS_SOURCE        auto (default: the client when it is there, else the prebuilt copy) | client | prebuilt

<project> = the folder above the git checkout. On the VPS only GW_GRAPHICS_OUT is set (models, stills, maps, web).

Routes (GET unless noted):
  overview                      sources found, counts
  index                         static/graphics/models.json + maps.json (what the tabs list)
  model?glb=REL                 a pre-built .glb (base64) from GW_GRAPHICS_OUT/models
  still?glb=REL                 its still picture (base64 PNG)
  mapimage?id=N&kind=terrain|minimap   the 1024x1024 world-aligned map picture (base64 PNG)
  browse?dir=REL                one client folder: sub-folders and files (size, kind)
  texture?path=REL[&frame=N]    a client picture as PNG (base64) + format, size, mode, frames
  atlas?path=REL                UI layout rectangles (UI/XML TexturePos + Rectangle) that use this UI texture
  convert?path=REL              any client model (.jcs / .x / .gwm) converted to .glb on the fly (cached)
  effects                       every effect file with the skills / statuses that use it
  effect?path=REL               one .gwm: elements with their placement and blend parameters + the .glb
  POST effect/save              {path, elements:[{index, position, scale, orientation, blend_src, blend_dst}]}
                                -> GW_GRAPHICS_OUT/edited/<path> (a copy; the client is never written). Works from
                                the prebuilt copy too (it patches web/raw/<path>, the original bytes).
"""
import base64
import hashlib
import io
import json
import os
import re
import struct
import sys
import threading

from . import common
from .common import Bad, NotFound

M = common.module(
    "graphics", "Graphics: models, maps, textures, effects",
    icon={"sprite": 1055},
    order=90,
    status="ready",
    description="Look at every monster, NPC, pet and mount in 3D, the maps, every client picture (textures, UI "
                "atlases with their layout) and the skill effects. Effects can be adjusted and saved as a copy.",
    needs_character=False,
)

HERE = os.path.dirname(os.path.abspath(__file__))
EDITOR = os.path.dirname(HERE)
PROJECT = os.path.normpath(os.path.join(EDITOR, "..", ".."))
STATIC_GFX = os.path.join(EDITOR, "static", "graphics")


def _env(name, default):
    v = os.environ.get(name)
    return os.path.abspath(os.path.expanduser(v)) if v else default


OUT = _env("GW_GRAPHICS_OUT", os.path.join(PROJECT, "Development", "w4", "graphics-out"))
CLIENT = _env("GW_CLIENT", os.path.join(PROJECT, "GodsWar.app", "Contents", "Resources", "Client"))
CLIENT_MODS = _env("GW_CLIENT_MODS", os.path.join(PROJECT, "GodsWar-2.51", "Client-Mods"))
WEB = os.path.join(OUT, "web")
SOURCE = (os.environ.get("GW_GRAPHICS_SOURCE") or "auto").strip().lower()
LOC_REL = "Localization/en_us"
EFFECT_FOLDERS = ("Effect", "Characters/effect", "Characters_New/effect", "Characters/PetUniteEffect",
                  "Characters_New/PetUniteEffect")
PICTURE_EXT = (".gwo", ".tga", ".dds", ".png", ".jpg", ".bmp", ".texture")
MODEL_EXT = (".jcs", ".x", ".gwm")
MAX_JSON_FILE = 40_000_000
_lock = threading.Lock()
_cache = {}


# ------------------------------------------------------------------------------------------------ helpers
def _have_client():
    return os.path.isdir(os.path.join(CLIENT, "Localization"))


def _have_web():
    return os.path.isfile(os.path.join(WEB, "index.json"))


def _source(need=True):
    """Where the Textures and Effects tabs read from: "client" (the client folder, converted on demand with the
    format readers and Pillow) or "prebuilt" (GW_GRAPHICS_OUT/web, plain files). None / Bad when neither is here."""
    if SOURCE in ("auto", "client") and _have_client():
        return "client"
    if SOURCE in ("auto", "prebuilt") and _have_web():
        return "prebuilt"
    if need:
        raise Bad("neither the game client (GW_CLIENT, now %s) nor the prebuilt pictures (%s, made by "
                  "tools/graphics/build_web.py) are on this server" % (CLIENT, WEB))
    return None


def _json_file(p):
    try:
        st = os.stat(p)
    except OSError:
        return {}
    key = ("json", p)
    with _lock:
        hit = _cache.get(key)
        if hit and hit[0] == st.st_mtime:
            return hit[1]
    with open(p, encoding="utf-8") as f:
        data = json.load(f)
    with _lock:
        _cache[key] = (st.st_mtime, data)
    return data


def _web(name):
    return _json_file(os.path.join(WEB, name))


def _rel(rel):
    rel = (rel or "").replace("\\", "/").strip("/")
    if "\0" in rel or rel.startswith("..") or "/../" in "/" + rel + "/":
        raise Bad("bad path")
    return rel


def _web_file(rel):
    """A file of the prebuilt copy. rel always comes from one of its own indexes, never straight from a request."""
    p = os.path.realpath(os.path.join(OUT, rel))
    if not p.startswith(os.path.realpath(OUT) + os.sep) or not os.path.isfile(p):
        raise NotFound("the prebuilt copy lacks %s (re-run tools/graphics/build_web.py and upload it)" % rel)
    return p


def _libs():
    """gwx (models, .gwm) and Pillow, or a clear error."""
    if not os.path.isfile(os.path.join(CLIENT_MODS, "gwx.py")):
        raise Bad("the format readers (gwx.py) are not on this server: set GW_CLIENT_MODS (now %s)" % CLIENT_MODS)
    if CLIENT_MODS not in sys.path:
        sys.path.append(CLIENT_MODS)
    try:
        import gwx  # noqa: F401
        from PIL import Image  # noqa: F401
    except ImportError as e:
        raise Bad("cannot load the format readers: %s (needs Pillow)" % e)
    return sys.modules["gwx"]


def _client_path(rel, must_exist=True):
    if not _have_client():
        raise Bad("the game client is not on this server: set GW_CLIENT (now %s)" % CLIENT)
    rel = (rel or "").replace("\\", "/").strip("/")
    if "\0" in rel or rel.startswith("..") or "/../" in "/" + rel + "/":
        raise Bad("bad path")
    p = os.path.realpath(os.path.join(CLIENT, rel))
    root = os.path.realpath(CLIENT)
    if p != root and not p.startswith(root + os.sep):
        raise Bad("path outside the client")
    if must_exist and not os.path.exists(p):
        raise NotFound("no such client file: %s" % rel)
    return p


def _out_file(sub, rel, pattern):
    if not re.match(pattern, rel or ""):
        raise Bad("bad file name")
    p = os.path.join(OUT, sub, rel)
    if not os.path.isfile(p):
        raise NotFound("not built yet: %s/%s (run tools/graphics/build_models.py and render_glb.mjs; "
                       "GW_GRAPHICS_OUT = %s)" % (sub, rel, OUT))
    return p


def _b64file(p):
    if os.path.getsize(p) > MAX_JSON_FILE:
        raise Bad("file too large to send (%d bytes)" % os.path.getsize(p))
    with open(p, "rb") as f:
        return base64.b64encode(f.read()).decode("ascii")


def _static_json(name):
    return _json_file(os.path.join(STATIC_GFX, name))


def _read_text(p):
    b = open(p, "rb").read()
    if b[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return b.decode("utf-16", "replace")
    if b[:3] == b"\xef\xbb\xbf":
        return b[3:].decode("utf-8", "replace")
    try:
        return b.decode("utf-8")
    except UnicodeDecodeError:
        return b.decode("gbk", "replace")


def _parse_ini(text):
    out, cur = {}, None
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("[") and "]" in s:
            cur = out.setdefault(s[1:s.index("]")].strip(), {})
        elif cur is not None and "=" in s:
            k, v = s.split("=", 1)
            cur[k.strip()] = v.strip()
    return out


def _png_b64(im):
    buf = io.BytesIO()
    im.save(buf, "PNG", optimize=False)
    return base64.b64encode(buf.getvalue()).decode("ascii"), len(buf.getvalue())


def _kind(name):
    low = name.lower()
    if low.endswith(PICTURE_EXT):
        return "picture"
    if low.endswith(MODEL_EXT):
        return "model"
    if low.endswith((".xml", ".ini", ".lua", ".dat", ".txt")):
        return "text"
    if low.endswith((".ogg", ".wav")):
        return "sound"
    return "other"


# ------------------------------------------------------------------------------------------------ overview / index
@M.get("overview")
def overview(ctx):
    models = _static_json("models.json")
    maps = _static_json("maps.json")
    out_models = os.path.isdir(os.path.join(OUT, "models"))
    return {
        "client": {"path": CLIENT, "found": _have_client()},
        "readers": {"path": CLIENT_MODS, "found": os.path.isfile(os.path.join(CLIENT_MODS, "gwx.py"))},
        "out": {"path": OUT, "models": out_models, "stills": os.path.isdir(os.path.join(OUT, "stills")),
                "maps": os.path.isdir(os.path.join(OUT, "maps"))},
        "source": _source(need=False),
        "web": {"path": WEB, "found": _have_web(), "built": (_web("index.json") or {}).get("built"),
                "counts": (_web("index.json") or {}).get("counts")},
        "counts": {k: len(models.get(k) or {}) for k in ("monster", "npc", "pet", "ride")} | {"maps": len(maps.get("maps") or {})},
    }


@M.get("index")
def index(ctx):
    return {"models": _static_json("models.json"), "maps": _static_json("maps.json"), "icons": _static_json("icons-extra.json")}


@M.get("model")
def model(ctx):
    rel = ctx.q("glb")
    p = _out_file("models", rel, r"^(monster|npc|pet|ride)/[A-Za-z0-9_.-]+\.glb$")
    return {"glb": rel, "bytes": os.path.getsize(p), "data": _b64file(p)}


@M.get("still")
def still(ctx):
    rel = ctx.q("glb")
    if not re.match(r"^(monster|npc|pet|ride)/[A-Za-z0-9_.-]+\.glb$", rel or ""):
        raise Bad("bad file name")
    p = _out_file("stills", rel[:-4] + ".png", r"^(monster|npc|pet|ride)/[A-Za-z0-9_.-]+\.png$")
    return {"glb": rel, "data": _b64file(p)}


@M.get("mapimage")
def mapimage(ctx):
    mid = ctx.qint("id", None, 0, 100000)
    if mid is None:                     # EDITOR-SMOKE: was a 500 (TypeError in "%d" % None) without ?id=
        raise Bad("id is required (the map number)")
    kind = ctx.q("kind", "terrain")
    if kind not in ("terrain", "minimap"):
        raise Bad("kind is terrain or minimap")
    p = _out_file("maps", "%d-%s.png" % (mid, kind), r"^\d+-(terrain|minimap)\.png$")
    return {"id": mid, "kind": kind, "data": _b64file(p)}


# ------------------------------------------------------------------------------------------------ client browser
def _skip(fn):
    return fn.startswith(".") or fn in ("_svn", ".svn")


def _list_dir(d):
    """One client folder -> (dirs [{name, entries}], files [{name, bytes, kind}]). Also used by build_web.py."""
    dirs, files = [], []
    for fn in sorted(os.listdir(d), key=str.lower):
        if _skip(fn):
            continue
        p = os.path.join(d, fn)
        if os.path.isdir(p):
            try:
                n = len(os.listdir(p))
            except OSError:
                n = 0
            dirs.append({"name": fn, "entries": n})
        else:
            files.append({"name": fn, "bytes": os.path.getsize(p), "kind": _kind(fn)})
    return dirs, files


@M.get("browse")
def browse(ctx):
    if _source() == "prebuilt":
        rel = _rel(ctx.q("dir", ""))
        rec = (_web("index.json").get("dirs") or {}).get(rel)
        if rec is None:
            raise NotFound("no such client folder: %s" % rel)
        # files: [name, bytes, kind, ready]; ready = 0 for a picture / model that build_web.py did not convert
        return {"dir": rel, "source": "prebuilt", "dirs": [{"name": n, "entries": c} for n, c in rec["dirs"]],
                "files": [{"name": f[0], "bytes": f[1], "kind": f[2], "ready": bool(f[3])} for f in rec["files"]]}
    d = _client_path(ctx.q("dir", ""))
    if not os.path.isdir(d):
        raise Bad("not a folder")
    dirs, files = _list_dir(d)
    rel = os.path.relpath(d, os.path.realpath(CLIENT)).replace(os.sep, "/")
    return {"dir": "" if rel == "." else rel, "source": "client", "dirs": dirs, "files": files}


def _open_picture(p, frame=0):
    """-> (PIL image RGBA, info). .gwo/.tga/.dds/.png through Pillow (a .gwo is a renamed TGA, DDS or PNG);
    .texture = water flipbook (u32 size + TGA per frame, gwmap.read_flipbook); .gwm = the images embedded per element."""
    _libs()
    from PIL import Image
    data = open(p, "rb").read()
    info = {"bytes": len(data)}
    low = p.lower()
    if low.endswith(".texture"):
        frames, o = [], 0
        while o + 4 <= len(data):
            n = struct.unpack_from("<I", data, o)[0]
            o += 4
            if n <= 0 or o + n > len(data):
                break
            frames.append(data[o:o + n])
            o += n
        if not frames:
            raise Bad("no frames in this .texture file")
        info["frames"] = len(frames)
        frame = max(0, min(frame, len(frames) - 1))
        info["frame"] = frame
        data = frames[frame]
        info["container"] = "flipbook of TGA frames"
    elif low.endswith(".gwm"):
        gwx = _libs()
        els, _ = gwx.read_gwm(data)
        imgs = [e.image for e in els if e.image]
        if not imgs:
            raise Bad("this effect has no embedded picture")
        info["frames"] = len(imgs)
        frame = max(0, min(frame, len(imgs) - 1))
        info["frame"] = frame
        data = imgs[frame]
        info["container"] = "effect element pictures"
    head = data[:4]
    info["format"] = ("DDS" if head == b"DDS " else "PNG" if head == b"\x89PNG" else "JPEG" if head[:2] == b"\xff\xd8"
                      else "BMP" if head[:2] == b"BM" else "TGA")
    if info["format"] == "DDS" and len(data) >= 88:
        fourcc = data[84:88]
        info["dds"] = fourcc.decode("latin-1") if fourcc.strip(b"\0") else "uncompressed"
        info["mipmaps"] = struct.unpack_from("<I", data, 28)[0]
    im = Image.open(io.BytesIO(data))
    im.load()
    info["size"] = list(im.size)
    info["mode"] = im.mode
    return im.convert("RGBA"), info


def _alpha_text(im):
    alpha = im.getchannel("A").getextrema()
    return "none" if alpha == (255, 255) else "%d-%d" % alpha


@M.get("texture")
def texture(ctx):
    if _source() == "prebuilt":
        rel = _rel(ctx.q("path"))
        rec = (_web("pictures.json") or {}).get(rel)
        if not rec:
            raise NotFound("no prebuilt picture for %s" % rel)
        info = {k: v for k, v in rec.items() if k != "files"}
        frame = max(0, min(ctx.qint("frame", 0, 0, 10000), len(rec["files"]) - 1))
        if len(rec["files"]) > 1:
            info["frame"] = frame
        fp = _web_file(rec["files"][frame])
        info.update(data=_b64file(fp), png_bytes=os.path.getsize(fp), mime="image/webp", path=rel, source="prebuilt")
        return info
    p = _client_path(ctx.q("path"))
    if not p.lower().endswith(PICTURE_EXT + (".gwm",)):
        raise Bad("not a picture file")
    im, info = _open_picture(p, ctx.qint("frame", 0, 0, 10000))
    if im.width * im.height > 4096 * 4096:
        raise Bad("picture too large")
    info["alpha"] = _alpha_text(im)
    info["data"], info["png_bytes"] = _png_b64(im)
    info["mime"] = "image/png"
    info["path"] = ctx.q("path")
    return info


def _ui_layout_index():
    """UI texture file name (lower case) -> [{file, element, x, y, w, h}] from Localization/en_us/UI/XML/*.xml.
    Element pictures: Texture="..." TexturePos="x,y" with the element's Rectangle size (buttons stack their 4 states
    downwards, MODDING.md 3.2). Elements without Texture use their window's / template's texture: not resolved here."""
    key = ("ui-layout", CLIENT)
    with _lock:
        if key in _cache:
            return _cache[key]
    xml_dir = os.path.join(CLIENT, LOC_REL, "UI", "XML")
    idx = {}
    if os.path.isdir(xml_dir):
        for fn in sorted(os.listdir(xml_dir)):
            if not fn.lower().endswith((".xml", ".lua")):
                continue
            try:
                t = _read_text(os.path.join(xml_dir, fn))
            except OSError:
                continue
            for m in re.finditer(r"<(\w+)\s([^<>]*?Texture\s*=\s*\"([^\"]+)\"[^<>]*)>", t):
                attrs = dict(re.findall(r"(\w+)\s*=\s*\"([^\"]*)\"", m.group(2)))
                pos = [int(float(x)) for x in re.findall(r"-?\d+(?:\.\d+)?", attrs.get("TexturePos", ""))[:2]]
                rect = [int(float(x)) for x in re.findall(r"-?\d+(?:\.\d+)?", attrs.get("Rectangle", ""))[:4]]
                if len(pos) < 2 or len(rect) < 4 or not attrs.get("Texture"):
                    continue
                tex = os.path.basename(attrs["Texture"].replace("\\", "/")).lower()
                idx.setdefault(tex, []).append({"file": fn, "element": m.group(1), "id": attrs.get("ID"),
                                                "x": pos[0], "y": pos[1], "w": rect[2] - rect[0], "h": rect[3] - rect[1]})
    with _lock:
        _cache[key] = idx
    return idx


def _item_icon_index():
    """Texture file name (lower case) -> [{id, key, x, y, w, h}]: ItemBaseAttribute.xml Texture + Icon (36x36)."""
    key = ("item-icons", CLIENT)
    with _lock:
        if key in _cache:
            return _cache[key]
    idx = {}
    try:
        t = _read_text(os.path.join(CLIENT, LOC_REL, "Settings", "Sys", "ItemBaseAttribute.xml"))
        for m in re.finditer(r"<(\w+)\s+ID=\"(\d+)\"([^>]*)>", t):
            a = dict(re.findall(r"(\w+)=\"([^\"]*)\"", m.group(3)))
            tex = os.path.basename(a.get("Texture", "").replace("\\", "/")).lower()
            xy = [int(x) for x in re.findall(r"-?\d+", a.get("Icon", ""))[:2]]
            if tex and len(xy) == 2:
                idx.setdefault(tex, []).append({"id": int(m.group(2)), "key": m.group(1), "x": xy[0], "y": xy[1], "w": 36, "h": 36})
    except OSError:
        pass
    with _lock:
        _cache[key] = idx
    return idx


@M.get("atlas")
def atlas(ctx):
    rel = _rel(ctx.q("path"))
    base = os.path.basename(rel).lower()
    if _source() == "prebuilt":
        a = _web("atlas.json") or {}
        rects, icons = (a.get("ui") or {}).get(base, []), (a.get("items") or {}).get(base, [])
    else:
        _client_path(rel)
        rects, icons = _ui_layout_index().get(base, []), _item_icon_index().get(base, [])
    return {"path": rel, "ui": rects[:3000], "ui_total": len(rects), "items": icons[:6000], "items_total": len(icons)}


REPORT_KEYS = ("meshes", "triangles", "vertices", "skins", "joints", "animations", "textures", "issues", "gwm_elements")


def _prebuilt_model(rel):
    rec = (_web("models.json") or {}).get(rel)
    if not rec:
        raise NotFound("no prebuilt 3D model for %s. The prebuilt copy holds every effect, monster, pet and mount "
                       "model; character part files are only converted where the game client is (the Mac)." % rel)
    return {"path": rel, "data": _b64file(_web_file(rec["glb"])), "report": rec.get("report") or {}, "source": "prebuilt"}


@M.get("convert")
def convert(ctx):
    rel = _rel(ctx.q("path"))
    if _source() == "prebuilt":
        return _prebuilt_model(rel)
    p = _client_path(rel)
    if not p.lower().endswith(MODEL_EXT):
        raise Bad("not a model file (.jcs, .x, .gwm)")
    gwx = _libs()
    st = os.stat(p)
    tag = hashlib.sha1(("%s|%d|%d" % (rel, st.st_size, int(st.st_mtime))).encode()).hexdigest()[:16]
    cdir = os.path.join(OUT, "cache")
    cp = os.path.join(cdir, tag + ".glb")
    rp = os.path.join(cdir, tag + ".json")
    if not os.path.exists(cp):
        rep, blob = gwx.convert_file(p, None)
        try:
            os.makedirs(cdir, exist_ok=True)
            with open(cp, "wb") as f:
                f.write(blob)
            with open(rp, "w") as f:
                json.dump({k: v for k, v in rep.items() if k in REPORT_KEYS}, f, default=str)
        except OSError:
            return {"path": rel, "data": base64.b64encode(blob).decode("ascii"), "report": {"triangles": rep.get("triangles")}}
    rep = json.load(open(rp)) if os.path.exists(rp) else {}
    rep["textures"] = [os.path.relpath(t, CLIENT) if isinstance(t, str) and t.startswith(CLIENT) else t
                       for t in (rep.get("textures") or [])]
    return {"path": rel, "data": _b64file(cp), "report": rep}


# ------------------------------------------------------------------------------------------------ effects
GWM_FIELDS = "position (3 floats at element+0x38+328), scale (+340), orientation x,y,z,w (+352), blend_src/dst (u32 4/5)"


def _gwm_layout(data):
    """Like gwx.read_gwm, but returns byte offsets of each textured element's two parameter blocks."""
    if len(data) < 8:
        raise Bad("gwm too short")
    n, n2 = struct.unpack_from("<II", data, 0)
    o = 8
    out = []
    for i in range(n):
        if o + 8 > len(data):
            raise Bad("gwm truncated")
        xs, ts = struct.unpack_from("<II", data, o)
        o += 8 + xs + ts
        out.append({"index": i, "p1": o, "p2": o + 0x38})
        o += 0x38 + 0x178
    if o > len(data):
        raise Bad("gwm truncated")
    return out, n2


def _effect_users():
    key = ("effect-users", CLIENT)
    with _lock:
        if key in _cache:
            return _cache[key]
    users = {}
    sysdir = os.path.join(CLIENT, LOC_REL, "Settings", "Sys")
    try:
        ini = _parse_ini(_read_text(os.path.join(sysdir, "Magic.ini")))
        for sec, v in ini.items():
            for k in ("EffectFile1", "EffectFile2", "EffectFile3", "EffectFile4"):
                f = (v.get(k) or "").strip().lower()
                if f and f not in ("null", "0"):
                    lst = users.setdefault(f, [])
                    nm = v.get("Name") or sec
                    if nm not in lst and len(lst) < 40:
                        lst.append(nm)
    except OSError:
        pass
    with _lock:
        _cache[key] = users
    return users


def _client_effect_list():
    users = _effect_users()
    out = []
    for folder in EFFECT_FOLDERS:
        d = os.path.join(CLIENT, folder)
        if not os.path.isdir(d):
            continue
        for fn in sorted(os.listdir(d), key=str.lower):
            if fn.lower().endswith(".gwm"):
                out.append({"path": folder + "/" + fn, "name": fn, "bytes": os.path.getsize(os.path.join(d, fn)),
                            "used_by": users.get(fn.lower(), [])})
    return out


def _edited_list():
    edited = []
    ed = os.path.join(OUT, "edited")
    for root, _, files in os.walk(ed):
        for fn in files:
            if fn.lower().endswith(".gwm"):
                edited.append(os.path.relpath(os.path.join(root, fn), ed).replace(os.sep, "/"))
    return sorted(edited), ed


@M.get("effects")
def effects(ctx):
    src = _source()
    out = (_web("effects.json") or {}).get("effects", []) if src == "prebuilt" else _client_effect_list()
    edited, ed = _edited_list()
    return {"effects": out, "edited": edited, "edited_dir": ed, "source": src}


def _effect_doc(rel, data):
    """The element table of one .gwm (bytes) -> dict without the .glb. Needs gwx; build_web.py stores the result."""
    gwx = _libs()
    els, trailer = gwx.read_gwm(data)
    lay, n2 = _gwm_layout(data)
    elements = []
    for i, e in enumerate(els):
        prm = e.params()
        prm.pop("p1_u32", None)
        elements.append({"index": i, "name": e.name, "textured": i < len(lay), "image_bytes": len(e.image or b""),
                         "x_bytes": len(e.xdata), "params": prm})
    return {"path": rel, "elements": elements, "untextured": n2, "trailer_bytes": len(trailer),
            "used_by": _effect_users().get(os.path.basename(rel).lower(), []), "fields": GWM_FIELDS}


@M.get("effect")
def effect(ctx):
    rel = _rel(ctx.q("path"))
    if not rel.lower().endswith(".gwm"):
        raise Bad("not an effect file (.gwm)")
    if _source() == "prebuilt":
        doc = ((_web("effects.json") or {}).get("docs") or {}).get(rel)
        if not doc:
            raise NotFound("no prebuilt effect %s" % rel)
        conv = _prebuilt_model(rel)
        return dict(doc, glb=conv["data"], report=conv.get("report"), source="prebuilt")
    p = _client_path(rel)
    doc = _effect_doc(rel, open(p, "rb").read())
    conv = convert(ctx)
    return dict(doc, glb=conv["data"], report=conv.get("report"), source="client")


def _floats(v, n, what):
    if not isinstance(v, list) or len(v) != n:
        raise Bad("%s needs %d numbers" % (what, n))
    try:
        out = [float(x) for x in v]
    except (TypeError, ValueError):
        raise Bad("%s must be numbers" % what)
    if any(x != x or abs(x) > 1e6 for x in out):
        raise Bad("%s out of range" % what)
    return out


def _gwm_fields(data, lay_i):
    """The editable fields of one textured element, read straight from the bytes (no gwx, no Pillow)."""
    p1, p2 = lay_i["p1"], lay_i["p2"]
    return {"position": list(struct.unpack_from("<3f", data, p2 + 328)), "scale": list(struct.unpack_from("<3f", data, p2 + 340)),
            "orientation": list(struct.unpack_from("<4f", data, p2 + 352)),
            "blend_src": struct.unpack_from("<I", data, p1 + 16)[0], "blend_dst": struct.unpack_from("<I", data, p1 + 20)[0]}


def _effect_original(rel):
    """Path of the unedited .gwm: the client's file, or the byte-identical copy in the prebuilt folder."""
    if _source() == "prebuilt":
        if rel not in ((_web("effects.json") or {}).get("docs") or {}):
            raise NotFound("no prebuilt effect %s" % rel)
        return _web_file("web/raw/" + rel)
    return _client_path(rel)


@M.post("effect/save")
def effect_save(ctx):
    """Patch placement / blend fields of textured elements and write the result as a COPY under
    GW_GRAPHICS_OUT/edited/<same relative path>. The client is never written; using the copy in a game is a
    separate, manual step (a client release). Only fixed-size fields are overwritten in place, so the file keeps
    its length and every other byte; that is checked before anything is written."""
    b = ctx.body or {}
    rel = _rel(b.get("path") or "")
    if not rel.lower().endswith(".gwm"):
        raise Bad("not an effect file (.gwm)")
    p = _effect_original(rel)
    ed_root = os.path.join(OUT, "edited")
    # continue from an earlier edited copy if asked
    base = p
    if b.get("from_edited"):
        cand = os.path.realpath(os.path.join(ed_root, rel))
        if cand.startswith(os.path.realpath(ed_root) + os.sep) and os.path.isfile(cand):
            base = cand
    with open(base, "rb") as f:
        before = f.read()
    data = bytearray(before)
    lay, _ = _gwm_layout(before)
    changes, touched = [], []
    for e in b.get("elements") or []:
        try:
            i = int(e.get("index"))
        except (TypeError, ValueError):
            raise Bad("element index")
        if not 0 <= i < len(lay):
            raise Bad("element %s is not a textured element" % e.get("index"))
        p1, p2 = lay[i]["p1"], lay[i]["p2"]
        if "position" in e:
            struct.pack_into("<3f", data, p2 + 328, *_floats(e["position"], 3, "position"))
            touched.append((p2 + 328, 12))
            changes.append("element %d position" % i)
        if "scale" in e:
            struct.pack_into("<3f", data, p2 + 340, *_floats(e["scale"], 3, "scale"))
            touched.append((p2 + 340, 12))
            changes.append("element %d scale" % i)
        if "orientation" in e:
            struct.pack_into("<4f", data, p2 + 352, *_floats(e["orientation"], 4, "orientation"))
            touched.append((p2 + 352, 16))
            changes.append("element %d orientation" % i)
        for k, off in (("blend_src", 16), ("blend_dst", 20)):
            if k in e:
                v = common.to_int(e[k], 0, 11, k)
                struct.pack_into("<I", data, p1 + off, v)
                touched.append((p1 + off, 4))
                changes.append("element %d %s" % (i, k))
    if not changes:
        raise Bad("nothing to change")
    data = bytes(data)
    # read the result back before writing it anywhere: same structure, the asked values, no other byte changed
    lay2, _ = _gwm_layout(data)
    if lay2 != lay or len(data) != len(before):
        raise Bad("read-back check failed: the element layout changed")
    mask = bytearray(len(data))
    for off, n in touched:
        mask[off:off + n] = b"\x01" * n
    if any(a != c and not m for a, c, m in zip(before, data, mask)):
        raise Bad("read-back check failed: bytes outside the edited fields changed")
    for e in b.get("elements") or []:
        prm = _gwm_fields(data, lay[int(e["index"])])
        for k in ("position", "scale", "orientation"):
            if k in e and any(abs(a - float(c)) > 1e-3 * max(1.0, abs(float(c))) for a, c in zip(prm[k], e[k])):
                raise Bad("read-back check failed for %s" % k)
        for k in ("blend_src", "blend_dst"):
            if k in e and prm[k] != int(e[k]):
                raise Bad("read-back check failed for %s" % k)
    if _source() == "client":          # the independent reader too, where it is installed
        els, _ = _libs().read_gwm(data)
        for e in b.get("elements") or []:
            prm = els[int(e["index"])].params()
            for k in ("blend_src", "blend_dst"):
                if k in e and prm[k] != int(e[k]):
                    raise Bad("read-back check (gwx) failed for %s" % k)
    out = os.path.realpath(os.path.join(ed_root, rel))
    if not out.startswith(os.path.realpath(ed_root) + os.sep) or (
            _have_client() and out.startswith(os.path.realpath(CLIENT) + os.sep)):
        raise Bad("refusing to write outside the edited-copies folder")
    try:
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "wb") as f:
            f.write(data)
    except OSError as e:
        raise Bad("cannot write the copy to %s: %s" % (ed_root, e))
    return {"written": out, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), "changes": changes,
            "note": "A copy only. The client still uses %s. Ship it in a client release to see it in the game." % rel}
