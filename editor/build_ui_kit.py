#!/usr/bin/env python3
"""build_ui_kit.py - the Godswar ORIGIN client's window art and layouts, for gwadmin-web.

Run on the Mac (it needs the client and Pillow); the outputs are checked in, so the
VPS needs neither:

    python3 -I build_ui_kit.py [CLIENT_DIR]            build everything into static/ui/
    python3 -I build_ui_kit.py [CLIENT_DIR] --dump ItemBagsExUI.xml [WINDOW]
                                                        print one layout's resolved element tree

CLIENT_DIR defaults to GodsWar-2.51/Reborn-Client/Client (the admin's client). Textures are read
with GodsWar-2.51/Client-Mods/gwo.py (a .gwo is a renamed TGA or DDS).

Writes, under static/ui/ (UI-STYLE.md there explains how the editor uses them):
  windows.json        every window in WINDOWS below: source layout, size, and every element
                      with its absolute rectangle (design pixels, window top-left = 0,0), type,
                      ID, picture (atlas + x,y), icon-on-top, font size, colour, text, alignment,
                      visibility, plus the detected slot grids (cells of equal size in rows)
  windows/<key>.png   the window rebuilt from its layout (frame, pictures, labels; the page of
                      the first tab of each tab bar). A reference picture, not a screenshot:
                      Lua-filled values, icons and hidden elements are not drawn.
  windows/<key>--<tab>.png  the same window with another tab page open (windows with tabs)
  sprites.png         reusable UI pieces cut from main.gwo (slot cells, buttons with their 4
                      state rows, tabs, check boxes, bars, arrows, the paperdoll figure, ...)
  frames/<name>.png   the window frames, assembled as 3x3 pictures for CSS border-image
  ui-kit.css          CSS classes for every sprite (.gw-<name>), the frames, and the colour
                      variables (--gw-<font.lua name>, --gw-q1..q20, --gw-g1..g25, --gw-apt1..)
  palette.json        the same colours as data (font.lua + ItemColor.xml), with their sources
  sprites.json        sprite name -> {x, y, w, h, frames, source atlas rectangle, used by}

Layout rules follow Client-RE/origin/UI-SYSTEM.md (sections 2-5): rectangles are l,t,r,b
relative to the parent; Template= copies the template at that point and later attributes
override it; a type-less element is only a group and its children belong to its parent;
children of a Tab are placed relative to the TabBar (measured: the 3x5 paperdoll of
ItemBagsExUI.xml is symmetric only that way); buttons and check boxes have 4 frames stacked
downwards; TexturePos 1024,1024 (outside the atlas) = no picture; OnScaleX/Y stretch.
"""
import copy
import importlib.util
import io
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

from PIL import Image, ImageChops, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))          # .../Godswar
GWO = os.path.join(ROOT, "GodsWar-2.51", "Client-Mods", "gwo.py")
OUT = os.path.join(HERE, "static", "ui")

TYPES = ["Element", "Text", "Button", "CheckBox", "MultilineEditBox", "EditBox", "ListItem", "ListBox",
         "ScrollBar", "Tab", "TabBar", "Container", "Window", "MessageBox", "ModalScreen", "HyperLink",
         "UIManager", "ChatEditBoxEx"]
CREATED = set(TYPES[1:13]) | {"ChatEditBoxEx"}          # types the factory really creates (UI-SYSTEM 3.4)

# key -> (layout file, top-level window, editor page that imitates it, title)
WINDOWS = [
    ("character",     "PersonalInfoUI.xml",  "PersonalInfo",     "Character sheet",            "Character info and attributes (C)"),
    ("target",        "TargetInfo.xml",      "TargetInfoWnd",    "Character sheet (read-only view)", "Inspect another player"),
    ("status",        "StatusUI.xml",        "StatusUI",         "Header of every character page", "Portrait, name, HP/MP bars (top-left HUD)"),
    ("bags",          "ItemBagsExUI.xml",    "ItemBags",         "Inventory",                  "Equipment paperdoll + bags (B)"),
    ("itemview",      "ItemBagsExUI.xml",    "ItemView",         "Item tooltip",               "Item tooltip panel"),
    ("bigbags",       "ItemBigBagsUI.xml",   "BigBags",          "Inventory (all bags)",       "All bags at once"),
    ("quickequip",    "QuickEquipUI.xml",    "QkEquipWin",       "Inventory - gear sets",      "Quick equip sets"),
    ("warehouse",     "StorageUI.xml",       "Storage",          "Warehouse",                  "Warehouse (storage boxes)"),
    ("forge",         "EquipForgeExUI.xml",  "EquipForge",       "Gear editor (forge / sockets)", "Forge, enchant and socket window"),
    ("forge_old",     "EquipForge.xml",      "EquipForge",       "Gear editor (reference)",    "Older forge window"),
    ("pet",           "PetUI.xml",           "PetWin",           "Pets - list",                "Pet list"),
    ("pet_info",      "PetInfo.xml",         "PetInfoWin",       "Pets - card",                "Pet info"),
    ("pet_detail",    "PetDetailUI.xml",     "PetDetailWin",     "Pets - card (stats, savvy)", "Pet details"),
    ("pet_skill",     "PetSkillUI.xml",      "PetSkillWin",      "Pets - skill cells",         "Pet skill bar"),
    ("pet_indenture", "PetIndentureUI.xml",  "IndentureWin",     "Pets - soul contract",       "Pet soul contract"),
    ("pet_bond",      "PetInosculateUI.xml", "BondWin",          "Pets - merge",               "Pet merge (inosculate)"),
    ("pet_samsara",   "PetSamsaraUI.xml",    "SamsaraWin",       "Pets - rebirth",             "Pet rebirth (samsara)"),
    ("pet_unite",     "PetUnite.xml",        "PetUniteWin",      "Pets - fusion",              "Pet fusion"),
    ("pet_work",      "PetWork.xml",         "PetWorkWin",       "Pets - work",                "Pet work"),
    ("mount",         "ItemBagsExUI.xml",    "ItemBags",         "Mounts",                     "Mount slot and gear (tab of the bag window)"),
    ("skills",        "SkillViewUI.xml",     "SkillViewUI",      "Skills & talents",           "Skill book and talent trees (K)"),
    ("skill_train",   "SkillTrain.xml",      "SkillTrainingWin", "Skills - learn",             "Skill trainer"),
    ("skill_choice",  "SkillChoice.xml",     "SkillChoiceWin",   "Skills - picker",            "Skill choice list"),
    ("life_skill",    "LiveSkillUI.xml",     "LiveSkillUI",      "Life skills",                "Life skills"),
    ("zodiac",        "Constellation.xml",   "ConstellationWin", "Zodiac",                     "Zodiac / constellation grids"),
    ("quest_log",     "QuestInfoUI.xml",     "QuestInfo",        "Quests",                     "Quest log (L)"),
    ("quest_view",    "QuestViewUI.xml",     "QuestView",        "Quests - detail",            "Quest text window"),
    ("guild",         "Consortia.xml",       "ConsortiaWin",     "Guild",                      "Guild"),
    ("titles",        "DesignateUI.xml",     "DesignateUI",      "Titles",                     "Titles"),
    ("mall",          "MallUIConfig.xml",    "MallWin",          "Item library / shop",        "Item mall"),
    ("npc_trade",     "NpcTrade.xml",        "NPCTrade",         "Item library (NPC shop look)", "NPC shop"),
    ("npc_dialog",    "NpcFun.xml",          "FirstWin",         "Holy Box / NPC services",    "NPC function dialog (Holy Package, Vestment Forger pages)"),
    ("relation",      "Relation.xml",        "RelationUI",       "Friends",                    "Friends / relations"),
    ("credit",        "CretitExchangeUI.xml","CretitExchange",   "Exchange points",            "Credit exchange"),
    ("tooltip",       "../Base/helper/UIHelper.xml", "UIHelper", "Hover help",                 "Tooltip window"),
    ("messagebox",    "MessageBoxUI.xml",    "MessageBoxUI",     "Confirm dialogs",            "OK / Cancel box"),
]


# ------------------------------------------------------------------ helpers

def load_gwo():
    spec = importlib.util.spec_from_file_location("gwo", GWO)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def read_text_file(path):
    raw = open(path, "rb").read()
    if raw[:3] == b"\xef\xbb\xbf":
        raw = raw[3:]
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("gbk", "replace")


def parse_xml(path):
    txt = read_text_file(path)
    txt = re.sub(r"<\?xml[^>]*\?>", "", txt, count=1).strip()
    try:
        return ET.fromstring(txt)
    except ET.ParseError:
        # TinyXML ignores anything after the root element (UI-SYSTEM 1.2)
        tag = re.search(r"<([A-Za-z_]\w*)", re.sub(r"<!--.*?-->", "", txt, flags=re.S)).group(1)
        end = txt.rfind("</%s>" % tag)
        return ET.fromstring(txt[:end + len(tag) + 3])


def nums(v):
    out = []
    for p in str(v).split(","):
        p = p.strip()
        if p == "":
            continue
        m = re.match(r"-?\d+", p)
        out.append(int(m.group(0)) if m else 0)
    return out


def lua_tables(path):
    """name = {k=v, ...} and name = "string" globals of a plain Lua data file."""
    tables, strings = {}, {}
    txt = read_text_file(path)
    for m in re.finditer(r"^[ \t]*([A-Za-z_]\w*)\s*=\s*\{([^}]*)\}", txt, re.M):
        d = {}
        for k, v in re.findall(r"(\w+)\s*=\s*(\"[^\"]*\"|[-\w.]+)", m.group(2)):
            v = v.strip('"')
            try:
                v = int(v)
            except ValueError:
                pass
            d[k] = v
        tables[m.group(1)] = d
    for m in re.finditer(r'^[ \t]*([A-Za-z_]\w*)\s*=\s*"((?:[^"\\]|\\.)*)"', txt, re.M):
        s = m.group(2).replace('\\"', '"').replace("\\n", "\n").replace("\\\\", "\\")
        strings[m.group(1)] = s
    return tables, strings


class Client:
    def __init__(self, client_dir):
        self.client = client_dir
        self.loc = os.path.join(client_dir, "Localization", "en_us")
        self.ui = os.path.join(self.loc, "UI")
        self.gwo = load_gwo()
        self.atlases = {}
        fonts, self.texts = lua_tables(os.path.join(self.ui, "Base", "font.lua"))
        self.fonts = {k: v for k, v in fonts.items() if "path" in v}
        self.colors = {k: v for k, v in fonts.items() if "r" in v}
        _, t = lua_tables(os.path.join(self.ui, "Base", "text.lua"))
        self.texts.update(t)
        lt = os.path.join(self.ui, "Base", "LuaText.lua")
        if os.path.exists(lt):
            _, t2 = lua_tables(lt)
            for k, v in t2.items():
                self.texts.setdefault(k, v)
        self.templates, self.missing_templates = {}, set()
        for f in ["Base/template.xml", "Base/special.xml", "XML/MessageBox_Template.xml",
                  "XML/Enemy_Template.xml", "XML/Event_Template.xml", "XML/MiniMap_Template.xml"]:
            p = os.path.join(self.ui, f)
            if os.path.exists(p):
                for el in parse_xml(p):
                    self.templates[el.tag] = self.describe(el, {})
        self.ttf = {}

    # ---- textures
    def atlas(self, path):
        if not path:
            return None
        p = path.replace("\\", "/")
        if p.startswith("./"):
            p = p[2:]
        full = os.path.join(self.client, p)
        if not os.path.exists(full):          # Windows / Wine are case-insensitive
            d, base = os.path.split(full)
            if os.path.isdir(d):
                for f in os.listdir(d):
                    if f.lower() == base.lower():
                        full = os.path.join(d, f)
                        break
        key = full.lower()
        if key not in self.atlases:
            if not os.path.exists(full):
                self.atlases[key] = None
            else:
                _, im = self.gwo.load(full)
                self.atlases[key] = im.convert("RGBA")
        return self.atlases[key]

    def atlas_name(self, path):
        return os.path.basename((path or "").replace("\\", "/")).lower()

    def font(self, size):
        if size not in self.ttf:
            path = os.path.join(self.client, "Fonts", "MainFont.ttf")
            try:
                self.ttf[size] = ImageFont.truetype(path, size)
            except OSError:
                self.ttf[size] = ImageFont.load_default()
        return self.ttf[size]

    # ---- layouts
    def describe(self, el, base):
        d = dict(base)
        for k, v in el.attrib.items():               # file order matters (UI-SYSTEM 3.3)
            if k == "Template":
                if v in self.templates:
                    d = copy.deepcopy(self.templates[v])
                else:
                    self.missing_templates.add(v)
                    d = {}
                d["_template"] = v
            elif k == "LocalTexture":
                d["Texture"] = "./Localization/en_us/" + v.lstrip("./")
            else:
                d[k] = v
        return d

    def layout(self, rel):
        p = os.path.normpath(os.path.join(self.ui, "XML", rel))
        return parse_xml(p)

    def color(self, name, default=(255, 255, 255, 255)):
        c = self.colors.get(name)
        if not c:
            return default
        return (c.get("r", 255), c.get("g", 255), c.get("b", 255), c.get("a", 255))

    def text_of(self, d):
        if "SText" in d:
            return self.texts.get(d["SText"], "")
        return d.get("Text", "")


# ------------------------------------------------------------------ element tree

def build_tree(cl, el, parent_origin, path, tabbar_origin=None, hidden=False, top=False):
    d = cl.describe(el, {})
    if top:
        d.pop("Visible", None)          # a top-level window is hidden until the game opens it
    typ = d.get("Type", "")
    created = typ in CREATED
    rect = nums(d.get("Rectangle", "0,0,0,0")) + [0, 0, 0, 0]
    l, t, r, b = rect[:4]
    sx = float(d.get("OnScaleX", "1") or 1)
    sy = float(d.get("OnScaleY", "1") or 1)
    w0, h0 = r - l, b - t
    ox, oy = parent_origin
    node = {
        "name": el.tag, "path": path + [el.tag], "type": typ or None, "created": created,
        "id": int(d["ID"]) if re.match(r"^-?\d+$", d.get("ID", "x")) else None,
        "template": d.get("_template"),
        "rect": [ox + l, oy + t, ox + l + round(w0 * sx), oy + t + round(h0 * sy)],
        "src_size": [w0, h0],
        "visible": d.get("Visible", "1") == "1" and not hidden,
        "hidden_self": d.get("Visible", "1") != "1",
        "d": d, "children": [],
    }
    if not created:
        node["rect"] = None
        child_origin = parent_origin                       # a group: children belong to the parent
    elif typ == "Tab":
        child_origin = tabbar_origin or parent_origin      # tab pages are placed in the TabBar
    else:
        child_origin = (ox + l, oy + t)
    next_tabbar = (ox + l, oy + t) if typ == "TabBar" else tabbar_origin
    for c in el:
        if c.tag in ("Script", "XML") or not isinstance(c.tag, str):
            continue
        node["children"].append(build_tree(cl, c, child_origin, node["path"], next_tabbar,
                                           hidden or node["hidden_self"]))
    return node


def walk(node, fn, depth=0):
    fn(node, depth)
    for c in node["children"]:
        walk(c, fn, depth + 1)


def find_window(cl, rel, name):
    root = cl.layout(rel)
    for el in root:
        if el.tag == name:
            return build_tree(cl, el, (0, 0), [], top=True)
    raise KeyError("%s not in %s" % (name, rel))


def normalise(win):
    """Shift so the window's own top-left is 0,0."""
    x0, y0 = win["rect"][0], win["rect"][1]

    def f(n, _):
        if n["rect"]:
            n["rect"] = [n["rect"][0] - x0, n["rect"][1] - y0, n["rect"][2] - x0, n["rect"][3] - y0]
    walk(win, f)
    win["screen_rect"] = [x0, y0, x0 + win["rect"][2], y0 + win["rect"][3]]


# ------------------------------------------------------------------ drawing

def crop(img, x, y, w, h):
    if img is None or w <= 0 or h <= 0 or x < 0 or y < 0 or x + w > img.width or y + h > img.height:
        return None
    return img.crop((x, y, x + w, y + h))


def overlap(a, b):
    """Intersection area / smaller area of two l,t,r,b rectangles."""
    w = min(a[2], b[2]) - max(a[0], b[0])
    h = min(a[3], b[3]) - max(a[1], b[1])
    if w <= 0 or h <= 0:
        return 0.0
    small = min((a[2] - a[0]) * (a[3] - a[1]), (b[2] - b[0]) * (b[3] - b[1])) or 1
    return w * h / small


def tint(im, rgba):
    if rgba == (255, 255, 255, 255):
        return im
    layer = Image.new("RGBA", im.size, rgba)
    return ImageChops.multiply(im, layer)


def nine_slice(cl, d, w, h):
    """Window frame from a template's Win* pieces (UI-SYSTEM 5.3).

    WinRect = corner size; corners are WinRect x WinRect at their positions; edges are
    1-pixel slices at WinTop/WinBottom (WinPixel tall, stretched across) and WinLeft/WinRight
    (WinPixel wide, stretched down); WinBody is a 1x1 fill stretched over the middle."""
    atlas = cl.atlas(d.get("Texture"))
    out = Image.new("RGBA", (max(w, 1), max(h, 1)), (0, 0, 0, 0))
    if atlas is None or "WinRect" not in d:
        return out
    c = nums(d["WinRect"])[0]
    p = nums(d.get("WinPixel", d["WinRect"]))[0]

    def piece(key, pw, ph):
        xy = nums(d.get(key, "-1,-1"))
        return crop(atlas, xy[0], xy[1], pw, ph)

    body = piece("WinBody", 1, 1)
    if body:
        out.paste(body.resize((max(1, w - 2 * p), max(1, h - 2 * p))), (p, p))
    top, bot = piece("WinTop", 1, p), piece("WinBottom", 1, p)
    left, right = piece("WinLeft", p, 1), piece("WinRight", p, 1)
    if top and w - 2 * c > 0:
        out.alpha_composite(top.resize((w - 2 * c, p)), (c, 0))
    if bot and w - 2 * c > 0:
        out.alpha_composite(bot.resize((w - 2 * c, p)), (c, h - p))
    if left and h - 2 * c > 0:
        out.alpha_composite(left.resize((p, h - 2 * c)), (0, c))
    if right and h - 2 * c > 0:
        out.alpha_composite(right.resize((p, h - 2 * c)), (w - p, c))
    for key, pos in (("WinTopLeft", (0, 0)), ("WinTopRight", (w - c, 0)),
                     ("WinBottomLeft", (0, h - c)), ("WinBottomRight", (w - c, h - c))):
        pc = piece(key, c, c)
        if pc:
            out.alpha_composite(pc, pos)
    return out


MARGIN = 60
FLAT = {"itemview"}       # windows whose text backgrounds the code resizes (ItemView = item tooltip)
# Containers carry Texture/TexturePos="0,0" in the layouts but are not drawn as a picture by the
# game (SkillViewUI's TabBar/ListBox/Container would otherwise cover the window with the corner
# art at main.gwo 0,0) [I]; their ScrollTexture is the scroll bar.
NO_PICTURE = {"TabBar", "ListBox", "Container", "ScrollBar"}
COLOR_CODE = re.compile(r"\|c([0-9A-Fa-f]{8})")


def draw_text(cl, canvas, d, rect, text, offset):
    if not text:
        return
    font_def = cl.fonts.get(d.get("Font", "MainFonts"), {"size": 12})
    size = int(font_def.get("size", 12))
    font = cl.font(size)
    color = cl.color(d.get("FontColor", "DEFAULT_TEXTCOLOR"))
    fmt = int(nums(d.get("TextFormat", "0"))[0]) if d.get("TextFormat") else 0
    to = nums(d.get("TextOffset", "0,0,0,0")) + [0, 0, 0, 0]
    l, t, r, b = rect
    l, t = l + to[0] + offset[0], t + to[1] + offset[1]
    r, b = r + offset[0], b + offset[1]
    # split |cAARRGGBB runs
    runs, cur, pos = [], color, 0
    for m in COLOR_CODE.finditer(text):
        if m.start() > pos:
            runs.append((text[pos:m.start()], cur))
        h = m.group(1)
        cur = (int(h[2:4], 16), int(h[4:6], 16), int(h[6:8], 16), int(h[0:2], 16) or 255)
        pos = m.end()
    runs.append((text[pos:], cur))
    plain = "".join(s for s, _ in runs)
    draw = ImageDraw.Draw(canvas)
    lines = plain.split("\n")
    if fmt & 16:                                  # word wrap
        wrapped = []
        for ln in lines:
            words, cur_ln = ln.split(" "), ""
            for wd in words:
                trial = (cur_ln + " " + wd).strip()
                if draw.textlength(trial, font=font) <= max(10, r - l) or not cur_ln:
                    cur_ln = trial
                else:
                    wrapped.append(cur_ln)
                    cur_ln = wd
            wrapped.append(cur_ln)
        lines = wrapped
    lh = size + 2
    total_h = lh * len(lines)
    if fmt & 4:
        y = t + (b - t - total_h) // 2
    elif fmt & 8:
        y = b - total_h
    else:
        y = t
    weight = int(d.get("TextWeight", "0") or 0)
    # colour per character: map plain index -> colour
    colmap = []
    for s, c in runs:
        colmap += [c] * len(s)
    idx = 0
    for ln in lines:
        lw = draw.textlength(ln, font=font)
        if fmt & 1:
            x = l + (r - l - lw) / 2
        elif fmt & 2:
            x = r - lw
        else:
            x = l
        cx = x
        for ch in ln:
            c = colmap[idx] if idx < len(colmap) else color
            if weight:
                for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                    draw.text((cx + dx, y + dy), ch, font=font, fill=(0, 0, 0, 255))
            draw.text((cx, y), ch, font=font, fill=c)
            cx += draw.textlength(ch, font=font)
            idx += 1
        idx += 1 if not (fmt & 16) else 0
        y += lh


def render(cl, win, active=None, flat=False):
    """Draw a window. active = {tabbar path tuple: tab index}; others show tab 0."""
    active = active or {}
    boxes = []

    def bb(n, _):
        if n["rect"] and n["visible"]:
            boxes.append(n["rect"])
    walk(win, bb)
    x0 = min(0, min(b[0] for b in boxes))
    y0 = min(0, min(b[1] for b in boxes))
    x1 = max(win["rect"][2], max(b[2] for b in boxes))
    y1 = max(win["rect"][3], max(b[3] for b in boxes))
    x0, y0 = max(x0, -MARGIN), max(y0, -MARGIN)          # parked off-window controls are left out
    x1, y1 = min(x1, win["rect"][2] + MARGIN), min(y1, win["rect"][3] + MARGIN)
    off = (-x0, -y0)
    canvas = Image.new("RGBA", (x1 - x0, y1 - y0), (0, 0, 0, 0))
    drawn_text = []

    def draw(n, page_ok=True):
        d = n["d"]
        if not n["visible"] and n is not win:
            return
        if n["rect"]:
            l, t, r, b = n["rect"]
            w, h = r - l, b - t
            pos = (l + off[0], t + off[1])
            typ = n["type"]
            pic = None
            if typ == "Window" and "WinRect" in d:
                pic = nine_slice(cl, d, w, h)
            elif d.get("Texture") and d.get("TexturePos") and typ not in NO_PICTURE:
                atlas = cl.atlas(d["Texture"])
                tx, ty = (nums(d["TexturePos"]) + [0, 0])[:2]
                sw, sh = n["src_size"]
                row = 0
                if typ == "Tab":
                    row = 2 if n.get("_active") else 0
                src = crop(atlas, tx, ty + row * sh, sw, sh)
                if src is None and row:
                    src = crop(atlas, tx, ty, sw, sh)
                if src is not None and flat and (sw > 64 or sh > 64):
                    # the code sizes these backgrounds to their text; show the fill colour only
                    src = src.crop((0, 0, 1, 1))
                if src is not None:
                    pic = src.resize((max(1, w), max(1, h))) if (w, h) != src.size else src
            if pic is not None and w > 0 and h > 0:
                if d.get("ImageColor"):
                    pic = tint(pic, cl.color(d["ImageColor"]))
                canvas.alpha_composite(pic, pos)
            if d.get("BtnTopTexture") and d.get("BtnTopPos") and d.get("BtnTopRect"):
                br = nums(d["BtnTopRect"])
                bp = nums(d["BtnTopPos"])
                top = crop(cl.atlas(d["BtnTopTexture"]), bp[0], bp[1], br[2] - br[0], br[3] - br[1])
                # icon.gwo placeholders are "empty slot" pictures the code replaces; draw main.gwo only
                if top is not None and cl.atlas_name(d["BtnTopTexture"]) == "main.gwo":
                    canvas.alpha_composite(top, (pos[0] + br[0], pos[1] + br[1]))
            txt = cl.text_of(d)
            if typ in ("Text", "Button", "CheckBox", "Tab", "ListItem") and txt.strip():
                # Lua shows one of several stacked help texts at a time; draw the first only
                if typ in ("Tab", "Button") or not any(overlap(n["rect"], o) > 0.5 for o in drawn_text):
                    draw_text(cl, canvas, d, n["rect"], txt, off)
                    drawn_text.append(n["rect"])
        if n["type"] == "TabBar":
            tabs = [c for c in n["children"] if c["type"] == "Tab"]
            want = active.get(tuple(n["path"]), 0)
            for i, c in enumerate(tabs):
                c["_active"] = (i == want)
            for c in n["children"]:
                if c["type"] == "Tab":
                    # tab button always, its page only when active
                    draw_tab(c)
                else:
                    draw(c)
            for c in tabs:
                if c["_active"]:
                    for cc in c["children"]:
                        draw(cc)
            return
        for c in n["children"]:
            draw(c)

    def draw_tab(c):
        kids = c["children"]
        c["children"] = []
        draw(c)
        c["children"] = kids

    draw(win)
    return canvas, off


# ------------------------------------------------------------------ analysis for windows.json

def grids(win):
    """Groups of same-size cells (slot grids) in rows/columns."""
    cells = []

    def f(n, _):
        if n["rect"] and n["type"] in ("Button", "CheckBox", "Text") and n["created"]:
            w, h = n["rect"][2] - n["rect"][0], n["rect"][3] - n["rect"][1]
            d = n["d"]
            if 30 <= w <= 48 and abs(w - h) <= 2 and (d.get("TexturePos") or d.get("BtnTopTexture")):
                cells.append(n)
    walk(win, f)
    groups = {}
    for n in cells:
        parent = tuple(n["path"][:-1])
        w = n["rect"][2] - n["rect"][0]
        groups.setdefault((parent, w), []).append(n)
    out = []
    for (parent, w), ns in groups.items():
        if len(ns) < 2:
            continue
        xs = sorted(set(n["rect"][0] for n in ns))
        ys = sorted(set(n["rect"][1] for n in ns))
        out.append({
            "parent": "/".join(parent), "cell": w, "count": len(ns),
            "columns": len(xs), "rows": len(ys),
            "x": xs, "y": ys,
            "ids": [n["id"] for n in ns][:400],
            "names": [n["name"] for n in ns][:400],
            "visible": sum(1 for n in ns if n["visible"]),
        })
    out.sort(key=lambda g: -g["count"])
    return out


def export_node(cl, n):
    d = n["d"]
    e = {"name": n["name"], "type": n["type"]}
    if n["id"] not in (None, -1):
        e["id"] = n["id"]
    if n["template"]:
        e["template"] = n["template"]
    if n["rect"]:
        e["rect"] = n["rect"]
    if not n["visible"]:
        e["visible"] = False
    if d.get("Texture") and d.get("TexturePos") and d.get("TexturePos") != "1024,1024":
        e["picture"] = {"atlas": cl.atlas_name(d["Texture"]), "xy": nums(d["TexturePos"])[:2],
                        "size": n["src_size"]}
        if d.get("OnScaleX") or d.get("OnScaleY"):
            e["picture"]["scale"] = [float(d.get("OnScaleX", 1)), float(d.get("OnScaleY", 1))]
    if "WinRect" in d:
        e["frame"] = n["template"] or "custom"
    if d.get("BtnTopTexture"):
        e["icon_on_top"] = {"atlas": cl.atlas_name(d["BtnTopTexture"]),
                            "xy": nums(d.get("BtnTopPos", "0,0"))[:2], "rect": nums(d.get("BtnTopRect", "0,0,0,0"))}
    txt = cl.text_of(d)
    if txt:
        e["text"] = txt
    if d.get("SText"):
        e["stext"] = d["SText"]
    if d.get("Font") and (txt or n["type"] in ("Text", "EditBox", "ListBox", "Container")):
        f = cl.fonts.get(d["Font"], {})
        e["font"] = {"name": d["Font"], "size": f.get("size")}
    if d.get("FontColor") and (txt or n["type"] in ("Text", "EditBox", "ListBox", "Container")):
        e["color"] = d["FontColor"]
    if d.get("ImageColor"):
        e["image_color"] = d["ImageColor"]
    if d.get("TextFormat") not in (None, ""):
        e["align"] = int(nums(d["TextFormat"])[0])
    for ev in ("OnClick", "OnHovered", "OnRClick", "OnChecked", "OnSelected", "OnTabChange"):
        if d.get(ev):
            e.setdefault("events", {})[ev] = d[ev]
    if d.get("Height") and n["type"] == "ListBox":
        e["row_height"] = int(nums(d["Height"])[0])
    kids = [export_node(cl, c) for c in n["children"]]
    if kids:
        e["children"] = kids
    return e


# ------------------------------------------------------------------ sprites

# name -> (atlas x, y, w, h, frames stacked downwards, note)
SPRITES = {
    # slots and cells
    "cell":            (0, 540, 42, 42, 3, "item / skill slot (T_SkillButton, every bag, warehouse, paperdoll and pet cell): normal, hover (blue rim), pressed; there is no disabled frame"),
    "cell-small":      (4, 544, 34, 34, 1, "inner 34x34 of the slot, for dense lists"),
    "skill-frame":     (50, 367, 42, 42, 1, "talent-tree cell frame (SkillViewUI SkillFrame, drawn under the icon)"),
    # window chrome
    "close":           (0, 80, 38, 37, 4, "T_CloseButton (big round X at a window's top right): normal, hover, pressed, disabled"),
    "close-small":     (124, 540, 21, 21, 4, "T_SmallCloseButton: 4 states"),
    "btn2":            (38, 80, 61, 27, 4, "T_Button2 (short button, 'OK', 'Save'): 4 states"),
    "btn2-small":      (304, 453, 50, 22, 4, "T_Button2_S (small button, 'Title', 'Upgrade'): 4 states"),
    "btn3":            (196, 80, 70, 27, 4, "T_Button3: 4 states"),
    "btn4":            (42, 540, 82, 27, 4, "T_Button4 (medium button; also the warehouse 'Storage Box' tabs): 4 states"),
    "btn8":            (59, 231, 110, 34, 4, "T_Button8 / login button (large): 4 states"),
    "btn9":            (0, 432, 152, 27, 4, "T_Button9 (long button): 4 states"),
    "btn-wide":        (953, 1, 70, 27, 4, "Consortia.xml guild buttons: 4 states"),
    "btn-drop":        (220, 260, 27, 27, 4, "square arrow button (DesignateUI): 4 states"),
    "tab":             (244, 450, 60, 23, 4, "window tab (EquipBag, SkillView, Consortia, Mall ...): normal, hover, selected, disabled"),
    "tab-wide":        (530, 673, 77, 23, 4, "wide tab (Constellation 'Acc. EXP' / 'Training'): 4 states"),
    "check":           (222, 189, 17, 18, 4, "T_NormalCheckBox as drawn by most windows: unchecked, (unused), checked, disabled"),
    "arrow-up":        (0, 228, 28, 22, 4, "T_UpButton (scroll up): 4 states"),
    "arrow-down":      (29, 228, 28, 22, 4, "T_DownButton (scroll down): 4 states"),
    "arrow-left":      (0, 316, 24, 29, 4, "T_LeftButton (page back): 4 states"),
    "arrow-right":     (25, 316, 24, 29, 4, "T_RightButton (page forward): 4 states"),
    "zoom-in":         (278, 375, 14, 14, 4, "T_ZoomUpBotton (+): 4 states"),
    "zoom-out":        (293, 375, 14, 14, 4, "T_ZoomDownBotton (-): 4 states"),
    "scroll-top":      (720, 30, 24, 24, 1, "scroll-bar track top cap (ListBox1Top etc.)"),
    "scroll-bottom":   (720, 0, 24, 24, 1, "scroll-bar track bottom cap"),
    "scroll-mid":      (720, 26, 24, 1, 1, "scroll-bar track middle (1 px, stretch it)"),
    "list-row":        (610, 620, 225, 21, 4, "quest-log list row (QuestInfoUI): normal (empty), hover, selected, disabled"),
    "edit-left":       (38, 188, 22, 22, 1, "number field left cap (money / amount boxes)"),
    "edit-right":      (60, 188, 22, 22, 1, "number field right cap"),
    "edit-mid":        (61, 188, 1, 22, 1, "number field middle (1 px, stretch it)"),
    # bars
    "bar-hp":          (779, 114, 116, 13, 1, "red bar (HP; pet Satiety / Amity / Lifetime in PetDetailUI)"),
    "bar-mp":          (779, 127, 116, 13, 1, "blue bar (MP)"),
    "bar-exp":         (780, 108, 114, 5, 1, "segmented green EXP bar"),
    # pictures
    "paperdoll":       (142, 701, 50, 119, 1, "the body silhouette behind the equipment slots (ItemBagsExUI BackGround)"),
    "set-gear":        (864, 165, 30, 30, 1, "gear-set icon on the Equip tab (SuButton)"),
    "zodiac-star-off": (485, 587, 18, 19, 1, "zodiac level star, not reached (Constellation 27x)"),
    "zodiac-star-on":  (567, 587, 18, 19, 1, "zodiac level star, reached"),
    "talent-arrow":    (265, 193, 12, 31, 1, "talent-tree arrow down"),
    "talent-line":     (302, 192, 4, 156, 1, "talent-tree long connector"),
    "talent-fork":     (94, 373, 158, 31, 1, "talent-tree fork: one to four (header row)"),
    "talent-fork-2":   (94, 405, 158, 19, 1, "talent-tree fork: four arrows"),
    "camp-sparta":     (893, 209, 59, 59, 1, "camp emblem, sword (StatusUI portrait ring) [which camp is inferred]"),
    "camp-athens":     (893, 268, 59, 59, 1, "camp emblem, temple (StatusUI portrait ring; the admin's Athenian character shows it)"),
    "hud":             (634, 874, 250, 82, 1, "the player status frame (StatusUI): portrait ring, name plate, HP / MP / EXP"),
    "coin-silver":     (57, 213, 14, 14, 1, "silver coin (money fields)"),
    "coin-gold":       (242, 227, 14, 14, 1, "gold coin (Mall 'Gold:')"),
    "divider-left":    (146, 174, 14, 10, 1, "section divider end (MIDdle)"),
    "star":            (435, 106, 28, 28, 1, "big star (server list 'recommended')"),
    "mall":            (380, 690, 52, 40, 4, "Mall button art: 4 states"),
}


def build_sprites(cl, used):
    main = cl.atlas("./Localization/en_us/UI/Texture/main.gwo")
    placed, x, y, rowh, W = {}, 0, 0, 0, 512
    pieces = []
    for name, (sx, sy, w, h, frames, note) in SPRITES.items():
        src = crop(main, sx, sy, w, h * frames)
        if src is None:
            frames = 1
            src = crop(main, sx, sy, w, h)
        if src is None:
            continue
        pieces.append((name, src, sx, sy, w, h, frames, note))
    pieces.sort(key=lambda p: -p[1].height)
    for name, src, sx, sy, w, h, frames, note in pieces:
        if x + src.width > W:
            x, y, rowh = 0, y + rowh + 2, 0
        placed[name] = {"x": x, "y": y, "w": w, "h": h, "frames": frames,
                        "source": "main.gwo %d,%d %dx%d" % (sx, sy, w, h * frames), "note": note,
                        "used_by": sorted(used.get((sx, sy), []))[:12]}
        x += src.width + 2
        rowh = max(rowh, src.height)
    H = y + rowh
    sheet = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    for name, src, *_ in pieces:
        sheet.paste(src, (placed[name]["x"], placed[name]["y"]))
    return sheet, placed


# content inset per frame: T_NormalWindow content starts at y 44 (tab bars, title text) and x 14
# (PersonalInfoUI StatusBack at 19,78; MIDdle dividers at x 14) although its corner art is 80 px
FRAME_INSET = {"T_NormalWindow": "44px 14px 14px 14px"}
FRAMES = ["T_NormalWindow", "T_SimpleWindow", "T_BgWindow", "T_EffectWindow", "T_ChatWindow1", "T_BubbleWnd"]


def build_zodiac(cl):
    """The 12 zodiac pictures (UI/Texture/000.gwo .. 011.gwo, 256x256), named like Constellation.lua's
    synconstype(): sign v -> 0vv.gwo and caption CON_L0_(23+v)."""
    os.makedirs(os.path.join(OUT, "zodiac"), exist_ok=True)
    signs = {}
    for v in range(12):
        im = cl.atlas("./Localization/en_us/UI/Texture/%03d.gwo" % v)
        if im is None:
            continue
        im.resize((128, 128), Image.LANCZOS).save(os.path.join(OUT, "zodiac", "%02d.png" % v), optimize=True)
        signs[v] = {"name": cl.texts.get("CON_L0_%d" % (23 + v), ""), "png": "zodiac/%02d.png" % v,
                    "source": "UI/Texture/%03d.gwo (256x256, saved at 128x128)" % v}
    return signs


def build_frames(cl):
    """Each window frame drawn at 3 x corner size, ready for CSS border-image (slice = corner)."""
    out = {}
    for name in FRAMES:
        d = cl.templates.get(name)
        if not d or "WinRect" not in d:
            continue
        c = nums(d["WinRect"])[0]
        size = 3 * c if c >= 8 else 3 * c + 8
        im = nine_slice(cl, d, size, size)
        out[name] = (im, c)
    return out


# ------------------------------------------------------------------ palette

def item_colors(cl):
    path = os.path.join(cl.loc, "Settings", "Sys", "ItemColor.xml")
    root = parse_xml(path)
    q, g, apt = {}, {}, {}
    for el in root.iter():
        a = el.attrib
        if el.tag.startswith("BaseLevel"):
            q[int(a["BaseLv"])] = {"name": a.get("BaseName"), "color": a.get("BaseColor")}
        elif el.tag.startswith("AppLevel"):
            g[int(a["AppendLv"])] = {"stars": int(a.get("AppendStar", 0)), "color": a.get("AppendColor"),
                                    "attribute_color": a.get("AppAttributeColor")}
        elif el.tag.startswith("Aptitude"):
            apt[int(a["BaseLv"])] = {"name": a.get("BaseName"), "color": a.get("BaseColor")}
    return q, g, apt


def hexc(rgba):
    return "#%02x%02x%02x" % tuple(rgba[:3])


REBORN_SQL = os.path.join(ROOT, "GodsWar-2.51", "emulators", "src", "godswar-server",
                          "database", "postgres", "003_item_quality.sql")
# Colours for constants the client's font.lua does not define (ItemColor.xml names them anyway).
# Values from the reborn tree's docs/client-gear-quality-grade-palette.md (its proposed QUALITY_Qnn
# palette, NOT applied to the admin's client) so the editor can still tell the levels apart.
UNDEFINED_FALLBACK = {"Astral_color": "#7f91d9", "Arcane_color": "#9178c8", "Transcendent_color": "#e8d66f",
                      "Primordial_color": "#e36033", "Boundless_color": "#ff3b30"}


def server_grades():
    """item_grade_levels rows (level, stars, color, attribute_color) from the reborn seed SQL."""
    out = {}
    if os.path.exists(REBORN_SQL):
        txt = open(REBORN_SQL, encoding="utf-8").read()
        sect = txt[txt.find("INSERT INTO item_grade_levels"):]
        for m in re.finditer(r"\((\d+),\s*(\d+),\s*'(\w+)',\s*'(\w+)'\)", sect):
            out[int(m.group(1))] = {"stars": int(m.group(2)), "color": m.group(3), "attribute_color": m.group(4)}
    return out


def build_palette(cl):
    q, g, apt = item_colors(cl)
    for lv, v in server_grades().items():          # the client file stops at grade 13; the server goes to 25
        if lv not in g:
            g[lv] = dict(v, server_only=True)
    undefined = sorted({v["color"] for v in list(q.values()) + list(g.values()) + list(apt.values())
                        if v["color"] not in cl.colors})
    pal = {"source": {"colors": "Localization/en_us/UI/Base/font.lua",
                      "items": "Localization/en_us/Settings/Sys/ItemColor.xml"},
           "colors": {k: hexc(cl.color(k)) for k in cl.colors},
           "quality": {}, "grade": {}, "aptitude": {}, "undefined_in_client": undefined}
    for lv, v in sorted(q.items()):
        c = v["color"]
        pal["quality"][lv] = {"name": v["name"], "constant": c,
                              "hex": hexc(cl.color(c)) if c in cl.colors else None}
    for lv, v in sorted(g.items()):
        c = v["color"]
        pal["grade"][lv] = {"stars": v["stars"], "constant": c,
                            "hex": hexc(cl.color(c)) if c in cl.colors else None,
                            "attribute_hex": hexc(cl.color(v["attribute_color"]))}
        if v.get("server_only"):
            pal["grade"][lv]["source"] = "reborn item_grade_levels (ItemColor.xml has no AppLevel%d)" % lv
    pal["fallback_for_undefined"] = UNDEFINED_FALLBACK
    for lv, v in sorted(apt.items()):
        c = v["color"]
        pal["aptitude"][lv] = {"name": v["name"], "constant": c,
                               "hex": hexc(cl.color(c)) if c in cl.colors else None}
    pal["fonts"] = {k: {"size": v.get("size")} for k, v in cl.fonts.items()}
    return pal


# ------------------------------------------------------------------ css

def css_name(s):
    return re.sub(r"[^a-z0-9]+", "-", s.lower()).strip("-")


def build_css(pal, sprites, frames):
    L = ["/* ui-kit.css - generated by build_ui_kit.py from the Godswar Origin client. Do not edit;",
         "   re-run the builder. Guide: static/ui/UI-STYLE.md */", "", ":root {"]
    for k, v in sorted(pal["colors"].items()):
        L.append("  --gw-%s: %s;" % (css_name(k), v))
    # quality: the client's own colour where defined; undefined constants fall back (UI-STYLE 4.1)
    fallback = UNDEFINED_FALLBACK
    for lv, v in pal["quality"].items():
        L.append("  --gw-q%s: %s;%s" % (lv, v["hex"] or fallback.get(v["constant"], "#ffffff"),
                                        "" if v["hex"] else "  /* %s is not defined in the client's font.lua */" % v["constant"]))
    for lv, v in pal["grade"].items():
        L.append("  --gw-g%s: %s;%s" % (lv, v["hex"] or fallback.get(v["constant"], "#ffffff"),
                                        "" if not v.get("source") else "  /* %s, %s */" % (v["source"], v["constant"])))
    L.append("  --gw-q0: #9a9a9a;  /* quality 0 has no ItemColor.xml entry (editor grey) */")
    for lv, v in pal["aptitude"].items():
        L.append("  --gw-apt%s: %s;" % (lv, v["hex"] or "#ffffff"))
    L += ["  --gw-panel: #0b1a22;", "  --gw-panel-2: #102633;", "  --gw-line: #1d4458;",
          "  --gw-title: var(--gw-yellow-textcolor);", "  --gw-label: var(--gw-base-headcolor);",
          "  --gw-value: var(--gw-ordinary-infocolor);", "  --gw-text-plain: var(--gw-default-textcolor);", "  --gw-good: var(--gw-green-textcolor);",
          "  --gw-bad: var(--gw-gwored);", "  --gw-font: Consolas, \"DejaVu Sans Mono\", Menlo, \"Microsoft YaHei\", monospace;",
          "}", ""]
    L.append(".gw-sprite { background-image: url(sprites.png); background-repeat: no-repeat; display: inline-block; }")
    for name, s in sorted(sprites.items()):
        L.append(".gw-%s { width: %dpx; height: %dpx; background-position: -%dpx -%dpx; }"
                 % (name, s["w"], s["h"], s["x"], s["y"]))
        if s["frames"] >= 2:
            L.append(".gw-%s:hover, .gw-%s.hover { background-position: -%dpx -%dpx; }"
                     % (name, name, s["x"], s["y"] + s["h"]))
        if s["frames"] >= 3:
            L.append(".gw-%s:active, .gw-%s.on, .gw-%s.selected { background-position: -%dpx -%dpx; }"
                     % (name, name, name, s["x"], s["y"] + 2 * s["h"]))
        if s["frames"] >= 4:
            L.append(".gw-%s.disabled, .gw-%s[disabled] { background-position: -%dpx -%dpx; }"
                     % (name, name, s["x"], s["y"] + 3 * s["h"]))
    L.append("")
    for name, (im, c) in frames.items():
        cls = css_name(name.replace("T_", ""))
        # the picture is drawn c px wide, but content may start nearer the edge, as in the game
        inset = FRAME_INSET.get(name, "%dpx" % c)
        L.append(".gw-frame-%s { border-style: solid; border-color: transparent; border-width: %s; "
                 "border-image: url(frames/%s.png) %d fill / %dpx stretch; }" % (cls, inset, name, c, c))
    L += ["", "/* quality / grade text helpers */"]
    for lv in pal["quality"]:
        L.append(".gw-q%s { color: var(--gw-q%s); }" % (lv, lv))
    for lv in pal["grade"]:
        L.append(".gw-g%s { color: var(--gw-g%s); }" % (lv, lv))
    for lv in pal["aptitude"]:
        L.append(".gw-apt%s { color: var(--gw-apt%s); }" % (lv, lv))
    L += ["", "/* the game's text look: small monospace, 1px black outline (TextWeight) */",
          ".gw-text { font: 12px/1.35 var(--gw-font); color: var(--gw-text-plain); }",
          ".gw-label { color: var(--gw-label); } .gw-value { color: var(--gw-value); }",
          ".gw-outline { text-shadow: -1px 0 #000, 1px 0 #000, 0 -1px #000, 0 1px #000; }",
          ".gw-tooltip { font: 12px/1.4 var(--gw-font); color: var(--gw-text-plain); background: rgba(6,14,20,.94);",
          "  border: 1px solid #3b7d98; box-shadow: 0 0 0 1px #000, 0 4px 14px #000a; padding: 6px 8px; max-width: 280px; }",
          ".gw-tooltip .tt-name { text-align: center; font-weight: 700; } .gw-tooltip .tt-green { color: var(--gw-green-textcolor); }",
          ".gw-tooltip .tt-bound { color: var(--gw-yellow-textcolor); } .gw-tooltip .tt-bad { color: var(--gw-gwored); }",
          ""]
    return "\n".join(L)


# ------------------------------------------------------------------ main

def dump(cl, rel, name=None):
    root = cl.layout(rel)
    for el in root:
        if el.tag in ("Script", "XML") or (name and el.tag != name):
            continue
        win = build_tree(cl, el, (0, 0), [], top=True)
        normalise(win)

        def p(n, depth):
            d = n["d"]
            bits = [n["type"] or "-", "#%s" % n["id"] if n["id"] not in (None, -1) else "",
                    str(n["rect"]) if n["rect"] else "", "pic=%s" % d.get("TexturePos") if d.get("TexturePos") else "",
                    "top=%s" % d.get("BtnTopPos") if d.get("BtnTopPos") else "",
                    repr(cl.text_of(d))[:50] if cl.text_of(d) else "", "" if n["visible"] else "HIDDEN"]
            print("  " * depth + n["name"], " ".join(b for b in bits if b))
        walk(win, p)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    client = args[0] if args and os.path.isdir(args[0]) else os.path.join(ROOT, "GodsWar-2.51", "Reborn-Client", "Client")
    cl = Client(client)
    if "--dump" in sys.argv:
        rest = sys.argv[sys.argv.index("--dump") + 1:]
        dump(cl, rest[0], rest[1] if len(rest) > 1 else None)
        return
    os.makedirs(os.path.join(OUT, "windows"), exist_ok=True)
    os.makedirs(os.path.join(OUT, "frames"), exist_ok=True)
    used = {}
    data = {"client": os.path.relpath(client, ROOT), "design_space": [1024, 768],
            "note": "rects are l,t,r,b in design pixels relative to the window's top-left; screen_rect is where "
                    "the game opens it on a 1024x768 screen. Generated by build_ui_kit.py.",
            "windows": {}}
    for key, rel, wname, page, title in WINDOWS:
        try:
            win = find_window(cl, rel, wname)
        except (KeyError, FileNotFoundError, ET.ParseError) as e:
            print("skip %s: %s" % (key, e))
            continue
        normalise(win)

        def u(n, _):
            d = n["d"]
            if d.get("TexturePos") and cl.atlas_name(d.get("Texture")) == "main.gwo":
                xy = tuple(nums(d["TexturePos"])[:2])
                used.setdefault(xy, set()).add("%s/%s" % (rel.split("/")[-1], n["name"]))
        walk(win, u)
        img, off = render(cl, win, flat=key in FLAT)
        img.save(os.path.join(OUT, "windows", key + ".png"), optimize=True)
        tabs_rendered = []
        # variants: each tab of every TabBar that is not nested inside another tab page
        bars = []

        def tb(n, depth):
            if n["type"] == "TabBar":
                bars.append(n)
        walk(win, tb)
        for bar in bars:
            tabs = [c for c in bar["children"] if c["type"] == "Tab"]
            if len(tabs) < 2 or any(tuple(o["path"]) == tuple(bar["path"][:len(o["path"])]) and o is not bar
                                    for o in bars):
                continue
            for i, t in enumerate(tabs[1:], 1):
                vimg, _ = render(cl, win, {tuple(bar["path"]): i})
                label = css_name(cl.text_of(t["d"]) or t["name"]) or t["name"].lower()
                fn = "%s--%s.png" % (key, label)
                vimg.save(os.path.join(OUT, "windows", fn), optimize=True)
                tabs_rendered.append({"tab": t["name"], "label": cl.text_of(t["d"]), "png": "windows/" + fn})
        data["windows"][key] = {
            "title": title, "editor_page": page, "layout": "UI/XML/" + rel if not rel.startswith("..") else "UI/" + rel[3:],
            "window": wname, "frame": win["template"], "screen_rect": win["screen_rect"],
            "size": [win["rect"][2], win["rect"][3]], "png": "windows/%s.png" % key, "png_offset": list(off),
            "tab_pages": tabs_rendered, "grids": grids(win), "elements": export_node(cl, win),
        }
        print("%-14s %-22s %4dx%-4d grids %s" % (key, rel.split("/")[-1], win["rect"][2], win["rect"][3],
                                                  ", ".join("%dx%d" % (g["columns"], g["rows"]) for g in data["windows"][key]["grids"][:4])))
    if cl.missing_templates:
        data["missing_templates"] = sorted(cl.missing_templates)
    with open(os.path.join(OUT, "windows.json"), "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
    sheet, sprites = build_sprites(cl, used)
    sheet.save(os.path.join(OUT, "sprites.png"), optimize=True)
    with open(os.path.join(OUT, "sprites.json"), "w", encoding="utf-8") as f:
        json.dump(sprites, f, ensure_ascii=False, indent=1)
    frames = build_frames(cl)
    for name, (im, c) in frames.items():
        im.save(os.path.join(OUT, "frames", name + ".png"), optimize=True)
    pal = build_palette(cl)
    pal["zodiac"] = build_zodiac(cl)
    pal["classes"] = {i: n for i, n in enumerate(["Warrior", "Champion", "Priest", "Mage"])}
    with open(os.path.join(OUT, "palette.json"), "w", encoding="utf-8") as f:
        json.dump(pal, f, ensure_ascii=False, indent=1)
    with open(os.path.join(OUT, "ui-kit.css"), "w", encoding="utf-8") as f:
        f.write(build_css(pal, sprites, frames))
    print("sprites %d, frames %d, colours %d, undefined colour constants: %s"
          % (len(sprites), len(frames), len(pal["colors"]), ", ".join(pal["undefined_in_client"]) or "none"))


if __name__ == "__main__":
    main()
