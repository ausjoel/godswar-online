"""B22 B26-T (the admin d121-d123): a second page on a teleporter menu (column npc_teleport_menu.parent_sub_id).

A page-1 button can open a page of its own (the capitals' "Go to the Northern lands", button 1104): the rows whose
"page" is that button's number are shown on it, in their order. One level only, at most 12 buttons shown per page
(the client window has 12 slots). A page button has no destination of its own, so it needs no map or point; it
needs client words like any other button (1104 comes with client 1.5.7).

Used by modules/_teleport_menu.py (_clean_rows calls page_buttons() and apply()); the page is
static/modules/teleport-pages.js. The game server must support pages (content describe: teleport_menus.page_support).
"""
from .common import Bad
from . import world as W


def _page_of(r, i):
    """The body row's page: None = page 1, else the page button's sub id."""
    p = r.get("page") if isinstance(r, dict) else None
    if p in (None, "", 0, "0", 1, "1"):
        return None
    return W._int(p, 2, 9999, "page of row %d" % (i + 1))


def page_buttons(body_rows):
    """Sub ids that open a page: referenced as another row's page, or marked page_button by the page."""
    out = set()
    for i, r in enumerate(body_rows or []):
        if not isinstance(r, dict):
            continue
        p = _page_of(r, i)
        if p is not None:
            out.add(p)
        if r.get("page_button"):
            out.add(W._int(r.get("sub"), 1, 9999, "button number"))
    return out


def apply(rows, body_rows, tm):
    """Set parent_sub_id on the cleaned rows (same order as body_rows) and check the pages. Returns warnings.

    Without any page in use the rows are left as they were (no parent_sub_id key), so a table or game server
    without page support still takes the write."""
    pages = {}
    for i, r in enumerate(body_rows):
        p = _page_of(r, i)
        if p is not None:
            pages.setdefault(p, []).append(i)
    marked = page_buttons(body_rows)
    if not marked:
        return []
    if not tm.get("page_support"):
        raise Bad("the running game server has no menu pages yet (it needs the B26-T game server)")
    by_sub = {row["sub_id"]: row for row in rows}
    warnings = []
    for p in sorted(marked):
        button = by_sub.get(p)
        if button is None:
            raise Bad("page %d: there is no button %d on page 1 to open it" % (p, p))
        bi = next(j for j, row in enumerate(rows) if row["sub_id"] == p)
        if _page_of(body_rows[bi], bi) is not None:
            raise Bad("button %d opens a page, so it must be on page 1 (one level of pages only)" % p)
        if button["target_map_id"] is not None:
            raise Bad("button %d opens a page, so it cannot also go to a map: clear its map and point" % p)
        if button["price"]:
            raise Bad("button %d opens a page; put prices on the buttons of the page instead" % p)
        shown = [j for j in pages.get(p, []) if not rows[j]["hidden"]]
        if not shown:
            raise Bad("page %d has no shown buttons: add some or remove the page button" % p)
        if len(shown) > tm["maximum_buttons"]:
            raise Bad("page %d shows %d buttons but the game window has room for %d: hide or move some"
                      % (p, len(shown), tm["maximum_buttons"]))
        if button["hidden"]:
            warnings.append("button %d is hidden, so players cannot reach its page" % p)
    for i, row in enumerate(rows):
        row["parent_sub_id"] = _page_of(body_rows[i], i)
    page1 = sum(1 for row in rows if not row["hidden"] and row["parent_sub_id"] is None)
    if page1 > tm["maximum_buttons"]:
        raise Bad("page 1 shows %d buttons but the game window has room for %d: hide or move some"
                  % (page1, tm["maximum_buttons"]))
    if page1 == 0:
        raise Bad("every page-1 button is hidden: the teleporter would show an empty menu")
    return warnings
