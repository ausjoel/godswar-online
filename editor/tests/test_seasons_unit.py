"""W3 seasonal events: the editor's season rows (Brisbane dates -> UTC window)."""
import sys, unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import events as E


class SeasonRowTests(unittest.TestCase):
    def test_brisbane_days_become_a_utc_window(self):
        r = E.season_row({"event_key": "thanksgiving", "enabled": True, "first_day": "2026-11-20", "last_day": "2026-11-30"})
        self.assertEqual(r["starts_at"], "2026-11-19T14:00:00+00:00")
        self.assertEqual(r["ends_at"], "2026-11-30T14:00:00+00:00")  # the whole last day, Brisbane
        self.assertTrue(r["enabled"] and r["yearly"])

    def test_no_dates_means_open_while_on(self):
        r = E.season_row({"event_key": "halloween", "enabled": False})
        self.assertIsNone(r["starts_at"]); self.assertIsNone(r["ends_at"]); self.assertFalse(r["enabled"])

    def test_refusals(self):
        for bad in ({"event_key": "nope"}, {"event_key": "easter", "first_day": "2026-04-01"},
                    {"event_key": "easter", "first_day": "2026-04-10", "last_day": "2026-04-01"},
                    {"event_key": "easter", "first_day": "2026-02-30", "last_day": "2026-03-01"},
                    {"event_key": "easter", "first_day": "x", "last_day": "y"}):
            with self.assertRaises(E.Bad):
                E.season_row(bad)

    def test_every_server_season_is_listed(self):
        self.assertEqual([k for k, _, _ in E.SEASONS], ["spring_festival", "carnival", "easter", "may_day", "igg_anniversary",
                                                         "mid_autumn", "oriental_cuisine", "halloween", "thanksgiving", "christmas"])


if __name__ == "__main__":
    unittest.main()
