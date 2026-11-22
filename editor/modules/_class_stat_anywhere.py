"""modules/_class_stat_anywhere.py - B22 d55 (the admin 2026-11-21, answer "allow"): the class-suit 5th stat (the class
stone, character_items.class_attribute1, ids 200-231) may sit on ANY piece of gear, not only on the class-suit pieces.

The database is the judge. Since the ClassSuitAttributes migration, CHECK ck_character_items_class_attribute_eligible_gear
refuses class_attribute1 on any prop_id that is not one of the 144 class-suit pieces. While that CHECK exists the editor
keeps the old rule (and says which SQL lifts it); once the lead has dropped it (sql/b22-d55-class-stone-any-gear.sql) the editor allows the class stone on every equippable item. Detected at run time
from pg_constraint, so the editor is right on every database (live, copies, a fresh install that re-creates the CHECK).

What the game server does with such an item (checked in the B22 source): the runtime stat projection reads
class_attribute1 of every worn item (PostgresCharacterRuntimeItemProjectionSql) and the world packet counts it
(PacketBuilder.PlayerWorld HasWorldItemAttribute); nothing filters by prop_id. Elemental attributes stay class-suit only
(their own CHECK, untouched).
"""
import time

CHECK_NAME = "ck_character_items_class_attribute_eligible_gear"
# the one-off SQL that lifts the rule: sql/b22-d55-class-stone-any-gear.sql (run by the lead as postgres)
_CACHE = {}           # database name -> (time, allowed)
TTL = 60.0


def allowed(rb):
    """True when the database lets class_attribute1 sit on any item (the eligible-gear CHECK is gone)."""
    if rb is None:
        return False
    key = getattr(rb, "db", None) or id(rb)
    hit = _CACHE.get(key)
    if hit and time.time() - hit[0] < TTL:
        return hit[1]
    try:
        n = rb.pg.json("(SELECT to_jsonb(count(*)) FROM pg_constraint WHERE conrelid = 'public.character_items'::regclass "
                       "AND conname = '%s')" % CHECK_NAME)
        ok = int(n or 0) == 0
    except Exception:          # noqa: BLE001 - no database / an older schema: keep the strict rule
        ok = False
    _CACHE[key] = (time.time(), ok)
    return ok


def forget():
    _CACHE.clear()


def refused_text(name, suit_count):
    return ("%s is not a class-suit piece. This database still only allows the class stone on the %d class-suit pieces; "
            "a one-off database step lifts that rule, then any gear can carry it." % (name, suit_count))


def eligible(rb, pid, meta, suit_items, gear=None):
    """(ok, reason): may this item carry a class stone? Gear only; any gear once the CHECK is gone."""
    if pid in suit_items:
        return True, None
    if gear is None:
        gear = int((meta or {}).get("slot", -1)) >= 0
    if not gear:
        return False, "%s is not gear: only gear can carry the class stone" % (meta or {}).get("name", "item #%s" % pid)
    if allowed(rb):
        return True, None
    return False, refused_text((meta or {}).get("name", "item #%s" % pid), len(suit_items))
