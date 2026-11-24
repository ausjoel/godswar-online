"""W15: gwadmin-web never writes character_talents.outbox_revision (strict talent outbox stream)."""
import re, sys, unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import reborn

T = "character_talents"
PK = ("user_id", "talent_id")


class TalentRevisionFrozen(unittest.TestCase):
    def test_counter_is_frozen_not_bumped(self):
        self.assertNotIn(T, reborn.SERVER_COUNTERS)
        self.assertIn("outbox_revision", reborn.SERVER_FROZEN[T])
        self.assertIn("outbox_revision", reborn.server_cols(T))

    def test_restore_dump_does_not_move_outbox_revision(self):
        # live 31 Oct: rank 6 -> 60 and revision 6 -> 7 with no outbox event
        before = {T: [{"user_id": 13, "talent_id": 150, "rank": 60, "outbox_revision": 6, "updated_at": "a"}]}
        now = {T: [{"user_id": 13, "talent_id": 150, "rank": 6, "outbox_revision": 6, "updated_at": "b"}]}
        sql = reborn.Reborn().restore_statements(before, now, tables=[(T, PK)])
        self.assertTrue(sql, "rank is still restored")
        self.assertTrue(all("\"outbox_revision\" =" not in s for s in sql), sql)
        before[T][0]["outbox_revision"] = 3        # the server moved it since the dump
        now[T][0]["outbox_revision"] = 9
        sql = reborn.Reborn().restore_statements(before, now, tables=[(T, PK)])
        self.assertTrue(all("\"outbox_revision\" =" not in s for s in sql), sql)


    def test_glossary_rank_write_bumps_nothing(self):
        from modules import common
        same, cross = common._bumps_for(T, ["rank"])
        self.assertNotIn("outbox_revision", same | cross)
        self.assertNotIn("outbox_revision", common.update_sql(T, {"user_id": 13, "talent_id": 150}, {"rank": 60}))

    def test_no_editor_sql_writes_outbox_revision(self):
        # live 31 Oct: talents page (modules/skills.py) set rank 60 with outbox_revision + 1;
        # live 12 Nov: "max character" (modules/maxer.py) set 18 talents to 100, each + 1. Both opened strict gaps.
        root = Path(__file__).resolve().parents[1]
        bad = []
        for f in [root / "reborn.py", *sorted((root / "modules").glob("*.py"))]:
            for n, line in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
                if re.search(r"outbox_revision\s*\+\s*1|SET\b.*\boutbox_revision|outbox_revision\s*=\s*(EXCLUDED|character_talents|tt)\.", line):
                    bad.append("%s:%d" % (f.name, n))
        self.assertEqual(bad, [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
