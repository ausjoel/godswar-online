#!/usr/bin/env python3
"""tests/test_inventory_probe.py - runs gwadmin-web/reborn_probe.py UNCHANGED in logic, with two test-only patches made
in memory (the shared file is not edited):

  * longer receive windows (a busy VPS answers the login after 3-4 s; the probe waits 2 s), set by --slow SECONDS;
  * the item records also decode item EXP @28, holy suit code @32, socket count @34, holy-stone codes @36+2n
    (effect x 100 + level - 1) and values @44+2n, and the Holy Box EXP @56 (/10)
    (S/Packets/PacketBuilder.ItemSerialization.cs WriteItemExtension / WriteHolyStoneValueRows).

Usage: same arguments as reborn_probe.py, plus [--slow 8]."""
import os
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
slow = 8.0
argv = sys.argv[1:]
if "--slow" in argv:
    i = argv.index("--slow")
    slow = float(argv[i + 1])
    del argv[i:i + 2]
src = open(os.path.join(HERE, "reborn_probe.py"), encoding="utf-8").read()
old_rec = '"bound": b[o + 26], "stack": b[o + 27]}'
new_rec = ('"bound": b[o + 26], "stack": b[o + 27], "exp": i32(b, o + 28), '
           '"suit": struct.unpack_from("<h", b, o + 32)[0], "sockets": struct.unpack_from("<h", b, o + 34)[0], '
           '"stones": [struct.unpack_from("<h", b, o + 36 + 2 * k)[0] for k in range(4)], '
           '"stone_values": [struct.unpack_from("<h", b, o + 44 + 2 * k)[0] for k in range(4)], '
           '"box_exp_x10": u32(b, o + 56) if len(b) >= o + 60 else None}')
assert old_rec in src, "reborn_probe.py item_record changed; update this wrapper"
src = src.replace(old_rec, new_rec)
for a, b in (("r = c.recv(2)\n", "r = c.recv(%s)\n" % max(2, slow)), ("c.recv(2)\n", "c.recv(%s)\n" % max(2, slow)),
             ("rd = [m for m in c.recv(2)", "rd = [m for m in c.recv(%s)" % max(2, slow)),
             ("r = g.recv(2)\n", "r = g.recv(%s)\n" % max(2, slow)), ("r2 = g.recv(2)\n", "r2 = g.recv(%s)\n" % max(2, slow)),
             ("allmsgs += g.recv(3)\n", "allmsgs += g.recv(%s)\n" % max(3, slow)),
             ('decode(g.recv(2), "walk")', 'decode(g.recv(%s), "walk")' % max(2, slow)),
             ('decode(g.recv(1.5), "warehouse")', 'decode(g.recv(%s), "warehouse")' % max(1.5, slow)),
             ("rw = g.recv(2.5)", "rw = g.recv(%s)" % max(2.5, slow))):
    src = src.replace(a, b)
sys.argv = [os.path.join(HERE, "reborn_probe.py")] + argv
src = src.replace('print(json.dumps(report, indent=1, default=str))', 'print(json.dumps({k: report.get(k) for k in ("enter", "equipment", "kitbag", "warehouse", "warehouse_messages", "kitbag_changes", "status")}, default=str)[:3000])')
exec(compile(src, sys.argv[0], "exec"), {"__name__": "__main__", "__file__": sys.argv[0]})
