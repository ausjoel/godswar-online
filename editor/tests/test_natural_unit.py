import os, sys, unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from modules import natural as N

BASE = {"fighter_job_lv": 50, "fighter_job_exp": 0, "fighter_level_sealed": False, "zodiac_level": 1, "zodiac_energy": 0, "profession": 0}
CONTENT = {"skills": [{"id": 1, "name": "Slash", "classes": [0], "minlv": 40}, {"id": 2, "name": "Late", "classes": [0], "minlv": 60}, {"id": 3, "name": "Bolt", "classes": [3], "minlv": 1}],
           "talents": [{"id": 10, "name": "A", "class": 0, "prefix": 0, "prefix_rank": 0}, {"id": 11, "name": "B", "class": 0, "prefix": 10, "prefix_rank": 5}, {"id": 12, "name": "C", "class": 3, "prefix": 0, "prefix_rank": 0}]}
META = {100: {"name": "Sword", "server": True, "overlap": 1, "slot": 1, "minlv": 60, "classes": [3]},
        200: {"name": "Potion", "server": True, "overlap": 99, "slot": -1, "minlv": 0, "classes": []},
        300: {"name": "Ghost", "server": False, "overlap": 1, "slot": -1, "minlv": 0, "classes": []}}
CLIENT = {"100": {"max_quality": 10, "max_grade": 12, "append_attributes": [40]}}
ATTRS = {"40": {"max": 25}, "50": {"max": 25}}


def item(**kw):
    r = dict(id=1, prop_id=200, stack=1, item_quality=1, item_grade=1, item_location=1, holy_socket_count=0)
    r.update(kw)
    return r


def rules(out):
    return sorted(f["rule"] for f in out)


class Natural(unittest.TestCase):
    def items(self, *rows):
        out = []
        N._item_checks(BASE, list(rows), META.__getitem__, CLIENT, ATTRS, 0, out)
        return out

    def test_clean_character(self):
        out = []
        N._base_checks(BASE, out)
        N._skill_checks(BASE, [{"skill_id": 1}], CONTENT, out)
        N._talent_checks(BASE, [{"talent_id": 10, "rank": 5}, {"talent_id": 11, "rank": 1}], CONTENT, out)
        self.assertEqual(out, [])

    def test_exp_at_threshold_but_sealed_is_fine(self):
        out = []
        N._base_checks(dict(BASE, fighter_job_exp=2 ** 32 - 1, fighter_level_sealed=True), out)
        self.assertEqual(out, [])
        N._base_checks(dict(BASE, fighter_job_exp=2 ** 32 - 1), out)
        self.assertEqual(rules(out), ["exp"])

    def test_level_200_exp_is_not_flagged(self):
        out = []
        N._base_checks(dict(BASE, fighter_job_lv=200, fighter_job_exp=4200000000), out)
        self.assertEqual(out, [])

    def test_skills(self):
        out = []
        N._skill_checks(BASE, [{"skill_id": 2}, {"skill_id": 3}, {"skill_id": 9}], CONTENT, out)
        self.assertEqual(rules(out), ["skill", "skill-class", "skill-level"])

    def test_server_granted_skills_are_not_flagged(self):
        out = []
        N._skill_checks(BASE, [{"skill_id": 2, "source": "mount-compatibility"}, {"skill_id": 3, "source": "starter"}], CONTENT, out)
        self.assertEqual(out, [])
        N._skill_checks(BASE, [{"skill_id": 2, "source": "gwadmin-web"}], CONTENT, out)
        self.assertEqual(rules(out), ["skill-level"])

    def test_talents(self):
        out = []
        N._talent_checks(BASE, [{"talent_id": 11, "rank": 1}, {"talent_id": 12, "rank": 1}, {"talent_id": 99, "rank": 1}, {"talent_id": 10, "rank": 101}, {"talent_id": 12, "rank": 0}], CONTENT, out)
        self.assertEqual(rules(out), ["talent", "talent-rank"])
        out = []
        N._talent_checks(BASE, [{"talent_id": 11, "rank": 1}, {"talent_id": 12, "rank": 1}], CONTENT, out)
        self.assertEqual(rules(out), ["talent-class", "talent-prefix"])

    def test_items(self):
        self.assertEqual(self.items(item()), [])
        self.assertEqual(rules(self.items(item(prop_id=300))), ["item"])
        self.assertEqual(rules(self.items(item(stack=100))), ["stack"])
        self.assertEqual(rules(self.items(item(prop_id=100, item_quality=11, item_grade=13))), ["grade", "quality"])
        self.assertEqual(rules(self.items(item(prop_id=100, item_location=0))), ["equip-class", "equip-level"])
        self.assertEqual(rules(self.items(item(prop_id=100, attribute1=50, attribute_level1=26))), ["attribute", "attribute-level"])
        self.assertEqual(rules(self.items(item(holy_socket_count=5))), ["sockets"])
        self.assertEqual(rules(self.items(item(holy_socket_count=1, holy_socket1_level=11))), ["holy-level"])

    def test_pets(self):
        pet = dict(id=1, name="p", activity_state="owned", level=1, experience=0, rank=1)
        out = []
        N._pet_checks([pet, dict(pet, activity_state="sealed", level=999)], {"maximum_rank": 100}, out)
        self.assertEqual(out, [])
        N._pet_checks([dict(pet, level=121, experience=N.PET_EXP_MAX + 1, rank=101)], {"maximum_rank": 100}, out)
        self.assertEqual(rules(out), ["pet-exp", "pet-level", "pet-rank"])


if __name__ == "__main__":
    unittest.main()
