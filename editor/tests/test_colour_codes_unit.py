"""build_colour_codes.py counts |cAARRGGBB codes in a client tree (UTF-16 / UTF-8 / GB18030, case-insensitive); the NPC greeting
module serves the committed JSON. No database. Run: python3 -I tests/test_colour_codes_unit.py"""
import importlib.util, json, os, sys, tempfile, unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
spec = importlib.util.spec_from_file_location("bcc", ROOT / "build_colour_codes.py"); B = importlib.util.module_from_spec(spec); spec.loader.exec_module(B)
from modules import npc_dialogue  # noqa: E402


class ColourCodes(unittest.TestCase):
    def test_scan_counts_case_insensitive_and_encodings(self):
        with tempfile.TemporaryDirectory() as d:
            loc = Path(d, "Localization", "en_us"); (loc / "Text" / "Quest").mkdir(parents=True); (loc / "UI").mkdir()
            (loc / "Text" / "NPCDescription.dat").write_bytes("A\tHi |cff39d8b8Zeus|cffffffff!\r\n".encode("utf-16"))
            (loc / "Text" / "Quest" / "1.dat").write_bytes("|cFF39D8B8x".encode("utf-8"))
            (loc / "UI" / "a.lua").write_bytes("--|cffff0000\xa1".encode("latin-1"))   # not UTF-8 -> GB18030 path
            total, text_only, files, sample = B.scan(str(loc))
            self.assertEqual(total["FF39D8B8"], 2); self.assertEqual(text_only["FF39D8B8"], 2); self.assertEqual(total["FFFF0000"], 1)
            self.assertEqual(text_only.get("FFFF0000", 0), 0); self.assertIn("Text/Quest/*.dat", files["FF39D8B8"])
            self.assertEqual(sample["FF39D8B8"], (True, "Zeus"))

    def test_committed_json_and_route(self):
        doc = json.loads((ROOT / "data" / "colour_codes.json").read_text())
        self.assertGreater(len(doc["colours"]), 10)
        self.assertEqual(doc["colours"][0]["code"], "FFFFFFFF")
        self.assertTrue(all(c["code"] == c["code"].upper() and c["rgb"] == "#" + c["code"][2:] for c in doc["colours"]))
        self.assertEqual(npc_dialogue.colours(None)["total_uses"], doc["total_uses"])
        old = npc_dialogue.COLOURS
        try:
            npc_dialogue.COLOURS = "/nonexistent/x.json"
            self.assertIn("error", npc_dialogue.colours(None))
        finally:
            npc_dialogue.COLOURS = old


if __name__ == "__main__":
    unittest.main(verbosity=2)
