"""Graphics module: the prebuilt source (no client, no format readers, no Pillow) and the effect copy writer.
Runs anywhere: it builds a tiny prebuilt folder in a temp dir. python3 editor/tests/test_graphics_unit.py"""
import base64, hashlib, json, os, struct, sys, tempfile, unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import graphics as G
from modules.common import Bad, NotFound


def gwm(elements):
    """A .gwm with textured elements only: [(xbytes, imagebytes, position)]"""
    out = struct.pack("<II", len(elements), 0)
    for x, img, pos in elements:
        p1 = bytearray(56); struct.pack_into("<II", p1, 16, 1, 1)
        p2 = bytearray(0x178); p2[1:5] = b"elem"
        struct.pack_into("<3f", p2, 328, *pos); struct.pack_into("<3f", p2, 340, 1, 1, 1); struct.pack_into("<4f", p2, 352, 0, 0, 0, 1)
        out += struct.pack("<II", len(x), len(img)) + x + img + bytes(p1) + bytes(p2)
    return out + b"TAIL"


class Ctx:
    def __init__(self, query=None, body=None):
        self.query, self.body = query or {}, body
    def q(self, k, d=None): return self.query.get(k, d)
    def qint(self, k, d=None, lo=None, hi=None): return int(self.query.get(k, d))


class PrebuiltSource(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        out = Path(self.tmp.name)
        web = out / "web"
        (web / "tex/Monster").mkdir(parents=True); (web / "tex/Map/w.texture").mkdir(parents=True)
        (web / "glb/Effect").mkdir(parents=True); (web / "raw/Effect").mkdir(parents=True)
        (web / "tex/Monster/a.gwo.webp").write_bytes(b"WEBP-A")
        for n in range(3): (web / ("tex/Map/w.texture/%d.webp" % n)).write_bytes(b"F%d" % n)
        (web / "glb/Effect/fx.gwm.glb").write_bytes(b"glTF-fx")
        self.raw = gwm([(b"xof 1", b"IMG", (0.0, 1.0, 0.0)), (b"xof 22", b"IMAGE2", (3.0, 4.0, 5.0))])
        (web / "raw/Effect/fx.gwm").write_bytes(self.raw)
        idx = {"built": "now", "counts": {"pictures": 2, "effects": 1}, "dirs": {
            "": {"dirs": [["Monster", 2], ["Effect", 1]], "files": [["config.ini", 10, "text", 0]]},
            "Monster": {"dirs": [], "files": [["a.gwo", 99, "picture", 1], ["part.jcs", 50, "model", 0]]},
            "Effect": {"dirs": [], "files": [["fx.gwm", len(self.raw), "model", 1]]}}}
        (web / "index.json").write_text(json.dumps(idx))
        (web / "pictures.json").write_text(json.dumps({
            "Monster/a.gwo": {"format": "TGA", "size": [4, 4], "mode": "RGBA", "alpha": "none", "bytes": 99, "files": ["web/tex/Monster/a.gwo.webp"]},
            "Map/w.texture": {"format": "TGA", "size": [8, 8], "mode": "RGB", "alpha": "none", "bytes": 500, "frames": 3, "container": "flipbook of TGA frames",
                              "files": ["web/tex/Map/w.texture/%d.webp" % n for n in range(3)]},
            "Monster/evil.gwo": {"format": "TGA", "size": [1, 1], "mode": "RGB", "alpha": "none", "bytes": 1, "files": ["../../etc/passwd"]}}))
        (web / "models.json").write_text(json.dumps({"Effect/fx.gwm": {"glb": "web/glb/Effect/fx.gwm.glb", "report": {"triangles": 4}}}))
        (web / "atlas.json").write_text(json.dumps({"ui": {"main.gwo": [{"file": "A.xml", "element": "B", "id": "1", "x": 1, "y": 2, "w": 3, "h": 4}]}, "items": {"item.gwo": [{"id": 5, "key": "k", "x": 0, "y": 0, "w": 36, "h": 36}]}}))
        (web / "effects.json").write_text(json.dumps({"effects": [{"path": "Effect/fx.gwm", "name": "fx.gwm", "bytes": len(self.raw), "used_by": ["Fire"]}],
            "docs": {"Effect/fx.gwm": {"path": "Effect/fx.gwm", "elements": [{"index": 0, "name": "elem", "textured": True, "params": {}}], "untextured": 0, "trailer_bytes": 4, "used_by": ["Fire"], "fields": ""}}}))
        boom = lambda *a, **k: (_ for _ in ()).throw(AssertionError("the prebuilt source must not load the client, gwx or Pillow"))
        self.patches = [patch.object(G, "OUT", str(out)), patch.object(G, "WEB", str(web)), patch.object(G, "CLIENT", str(out / "no-client")),
                        patch.object(G, "SOURCE", "auto"), patch.object(G, "_libs", boom), patch.object(G, "_client_path", boom)]
        for p in self.patches: p.start()
        G._cache.clear()

    def tearDown(self):
        for p in self.patches: p.stop()
        self.tmp.cleanup()

    def test_source_is_prebuilt_without_a_client_and_overview_says_so(self):
        self.assertEqual(G._source(), "prebuilt")
        ov = G.overview(Ctx())
        self.assertEqual(ov["source"], "prebuilt"); self.assertTrue(ov["web"]["found"]); self.assertFalse(ov["client"]["found"])

    def test_no_source_gives_a_reason_naming_both(self):
        with patch.object(G, "WEB", "/nonexistent/web"):
            with self.assertRaises(Bad) as e: G.browse(Ctx({"dir": ""}))
            self.assertIn("build_web.py", str(e.exception)); self.assertIsNone(G._source(need=False))

    def test_browse_lists_folders_and_marks_unconverted_files(self):
        r = G.browse(Ctx({"dir": "Monster"}))
        self.assertEqual([(f["name"], f["ready"]) for f in r["files"]], [("a.gwo", True), ("part.jcs", False)])
        self.assertEqual(G.browse(Ctx({"dir": ""}))["dirs"][0], {"name": "Monster", "entries": 2})
        for bad in ("nope", "../Monster"):
            with self.assertRaises((NotFound, Bad)): G.browse(Ctx({"dir": bad}))

    def test_texture_returns_webp_and_frames(self):
        t = G.texture(Ctx({"path": "Monster/a.gwo"}))
        self.assertEqual((t["mime"], base64.b64decode(t["data"]), t["size"], t["format"]), ("image/webp", b"WEBP-A", [4, 4], "TGA"))
        self.assertNotIn("files", t)
        t = G.texture(Ctx({"path": "Map/w.texture", "frame": "2"}))
        self.assertEqual((base64.b64decode(t["data"]), t["frames"], t["frame"]), (b"F2", 3, 2))
        self.assertEqual(G.texture(Ctx({"path": "Map/w.texture", "frame": "99"}))["frame"], 2)
        with self.assertRaises(NotFound): G.texture(Ctx({"path": "Monster/missing.gwo"}))

    def test_an_index_entry_cannot_point_outside_the_output_folder(self):
        with self.assertRaises(NotFound): G.texture(Ctx({"path": "Monster/evil.gwo"}))

    def test_atlas_model_and_effect(self):
        a = G.atlas(Ctx({"path": "Localization/en_us/UI/Texture/Main.gwo"}))
        self.assertEqual((a["ui_total"], a["ui"][0]["element"], a["items_total"]), (1, "B", 0))
        self.assertEqual(G.atlas(Ctx({"path": "x/item.gwo"}))["items"][0]["id"], 5)
        m = G.convert(Ctx({"path": "Effect/fx.gwm"}))
        self.assertEqual((base64.b64decode(m["data"]), m["report"]["triangles"]), (b"glTF-fx", 4))
        with self.assertRaises(NotFound) as e: G.convert(Ctx({"path": "Characters/female_body_2100_chest.jcs"}))
        self.assertIn("character part", str(e.exception))
        self.assertEqual(G.effects(Ctx())["effects"][0]["used_by"], ["Fire"])
        fx = G.effect(Ctx({"path": "Effect/fx.gwm"}))
        self.assertEqual((base64.b64decode(fx["glb"]), fx["elements"][0]["name"], fx["source"]), (b"glTF-fx", "elem", "prebuilt"))

    def test_effect_save_writes_a_copy_and_changes_only_the_asked_fields(self):
        r = G.effect_save(Ctx(body={"path": "Effect/fx.gwm", "elements": [{"index": 1, "position": [1.5, 2.5, -3.0], "blend_dst": 5}]}))
        out = Path(G.OUT) / "edited/Effect/fx.gwm"
        self.assertEqual(os.path.realpath(r["written"]), os.path.realpath(out))
        new = out.read_bytes()
        self.assertEqual(r["sha256"], hashlib.sha256(new).hexdigest())
        self.assertEqual((Path(G.WEB) / "raw/Effect/fx.gwm").read_bytes(), self.raw)          # the original is untouched
        lay, _ = G._gwm_layout(new)
        f = G._gwm_fields(new, lay[1])
        self.assertEqual((f["position"], f["blend_dst"], f["blend_src"], f["scale"]), ([1.5, 2.5, -3.0], 5, 1, [1.0, 1.0, 1.0]))
        self.assertEqual(G._gwm_fields(new, lay[0]), G._gwm_fields(self.raw, lay[0]))
        diff = [i for i, (a, b) in enumerate(zip(self.raw, new)) if a != b]
        p1, p2 = lay[1]["p1"], lay[1]["p2"]
        self.assertTrue(all(p2 + 328 <= i < p2 + 340 or p1 + 20 <= i < p1 + 24 for i in diff) and len(new) == len(self.raw))
        self.assertEqual(G.effects(Ctx())["edited"], ["Effect/fx.gwm"])
        # continue from the copy
        G.effect_save(Ctx(body={"path": "Effect/fx.gwm", "from_edited": True, "elements": [{"index": 0, "scale": [2, 2, 2]}]}))
        new2 = out.read_bytes()
        self.assertEqual((G._gwm_fields(new2, lay[0])["scale"], G._gwm_fields(new2, lay[1])["blend_dst"]), ([2.0, 2.0, 2.0], 5))

    def test_effect_save_refusals(self):
        for body, word in (({"path": "Effect/fx.gwm", "elements": []}, "nothing"), ({"path": "Effect/fx.gwm", "elements": [{"index": 7, "scale": [1, 1, 1]}]}, "not a textured"),
                           ({"path": "Effect/fx.gwm", "elements": [{"index": 0, "scale": [1, 1]}]}, "needs 3"), ({"path": "Effect/fx.gwm", "elements": [{"index": 0, "position": [1, 2, float("nan")]}]}, "range"),
                           ({"path": "Effect/fx.jcs", "elements": []}, "not an effect"), ({"path": "../raw/Effect/fx.gwm", "elements": []}, "bad path")):
            with self.assertRaises(Bad) as e: G.effect_save(Ctx(body=body))
            self.assertIn(word, str(e.exception))
        with self.assertRaises(NotFound): G.effect_save(Ctx(body={"path": "Effect/other.gwm", "elements": [{"index": 0, "scale": [1, 1, 1]}]}))
        self.assertFalse((Path(G.OUT) / "edited").exists())


class ShippedIndex(unittest.TestCase):
    """static/graphics/models.json: the old / new sets the Models tab switches between."""
    M = json.loads((Path(__file__).resolve().parents[1] / "static/graphics/models.json").read_text())

    def test_every_dressed_npc_has_both_sets_and_the_game_one_is_first(self):
        self.assertIn(self.M["avatar_set"]["in_game"], ("old", "new"))
        av = [v for v in self.M["npc"].values() if v.get("kind") == "avatar"]
        self.assertGreater(len(av), 500)
        for v in av:
            self.assertEqual(v["set"], self.M["avatar_set"]["in_game"])
            other = "new" if v["set"] == "old" else "old"
            self.assertTrue(v["alt"][other]["glb"].startswith("npc/avatar")); self.assertNotEqual(v["alt"][other]["glb"], v["glb"])
            self.assertIsInstance(v["alt"][other]["t"], int)

    def test_monster_families_hold_old_and_new_models(self):
        fam = self.M["families"]
        self.assertIn("monster_skeleton", fam)
        for sets in fam.values():
            self.assertTrue(sets["old"] and sets["new"])
            self.assertTrue(all(o["glb"].startswith("monster/") and isinstance(o["t"], int) for st in sets.values() for o in st))
        new = [v for v in self.M["monster"].values() if v.get("set") == "new"]
        self.assertTrue(new and all("new" in v["model"].lower() for v in new))


if __name__ == "__main__":
    unittest.main()
