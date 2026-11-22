# Package C (wave 4): what the lead adds by hand

The Operations module (`modules/operations.py`, `static/modules/operations.{js,css}`) is picked up automatically
(any `modules/*.py`). These shared files need the lead:

1. **`static/shell.js`**: apply `ops/shell-w4c.patch` (`git apply editor/ops/shell-w4c.patch` from the repo root).
   It removes the "Editor parts (demo)" sidebar link, its `#/kit` route and the `kit()` function, and replaces the
   agent-status pill script (the one that shows "Mac not reporting") with a comment, so the header link always reads
   "Development progress". The Mac job that publishes `static/agent-status.json` can then be switched off.
   `static/modules/library-shell.patch` line 19 has the old `kit` route as context; refresh that patch if it is reapplied.
2. **`MODULES.md`**:
   - line 153: delete ``Demo of every shared part: the sidebar's "Editor parts (demo)" (`#/kit`).``
   - module list: add `operations` - "Operations: gw251 commands, resource monitor, realms, change log (order 95)".
3. **`schema/write_rules.json`**: replace `tables.server` with `ops/write_rules-server.proposed.json`
   (`ip_address`, `game_port`, `server_limit`, `enabled`, `display_order`, `recommended` become `edit_advanced`;
   `identifier`, `id`, time-zone columns stay read-only). Until then, Operations -> Servers can add and remove realms
   and rename them, but other edits answer 403 "not editable" (tested in `tests/test_operations_unit.py`).
4. **Database** (as owner `godswar`): `ops/realm-grants.sql`.
5. **VPS root helper**: `ops/server-control/INSTALL.md` (helper v2 + restore unit; the sudoers file is a fallback, not
   for install).
6. **Change log drawer** (`static/shared/`, `GW.changes`): add a link "Clear or export the log" to
   `#/operations/changes` at the top of the drawer, since that is where the admin looks. Clearing keeps records still waiting for
   a restart; their Undo stays guarded by the after-image check (409 "changed since") even when later records on the same
   rows were archived, so a forced Undo (overwrite) is the only way to lose a newer edit.
7. **Release scripts**: copy `editor/data/colour_codes.json` with the editor (it is data, built by
   `build_colour_codes.py` on the Mac).

Tests: `python3 -I tests/test_operations_unit.py`, `python3 -I tests/test_server_control_unit.py`,
`python3 -I tests/test_colour_codes_unit.py`, `node tests/test_operations_ui.cjs`, `node tests/test_npc_colour_tags_ui.cjs`.

---

# Package B (wave 5): what the lead adds by hand

New / changed in the editor: `modules/gm_powers.py` + `static/modules/gm_powers.{js,css}` (a new page, picked up
automatically), `modules/operations.py` + `static/modules/operations.{js,css}` (guided Add / Close / Delete server),
`data/gm_powers_inventory.json` (built by `build_gm_powers_inventory.py`), `schema/write_rules.json` (two edits, below),
`ops/server-control/*` (helper v3), `ops/GM-POWERS.md`.

1. **`MODULES.md`** module list: add `gm_powers` - "GM powers: who is a GM, what a GM can do, per-command switches
   (order 96)"; change the `operations` line to "... realms (guided Add / Close / Delete server), change log".
2. **`static/shell.js`**: nothing. (If the sidebar groups pages by hand anywhere, `gm_powers` belongs next to
   `operations`.)
3. **`schema/write_rules.json`** (already edited on this branch; check for conflicts with other packages):
   - `tables.character_base.fields.GM`: was `read_only / "not read by this server"` (stale since
     `GODSWAR_CHARACTER_GM_FLAGS`), now `edit_advanced`, `applies: now`, enum 0/1.
   - new `tables.gm_power_policy` (`edit`, `applies: hot_reload`).
4. **Release scripts**: copy `editor/data/gm_powers_inventory.json` with the editor. The game server release creates
   `gm_power_policy` + `gm_power_policy_log` at its first start and grants `gweditor` itself (it owns the tables); no
   SQL step, no migration id. `compatible()` / the expected migration head do not change.
5. **Database grants**: none new for GM powers. For Add server the W4 grants in `ops/realm-grants.sql` are enough
   (`INSERT, DELETE ON server`, `SELECT, INSERT ON server_time_zone_audit`).
6. **VPS root helper v3**: `ops/server-control/INSTALL.md`, section "Helper v3".
7. **Fixed on the way (W4 code that had never met a real database, found on the rig)**: `REALM_GUARD` had no closing
   `;`, so EVERY Servers save, add and remove failed with `syntax error at or near "COMMIT"`; the "Used by" counts cast
   a bigint to jsonb and failed, so every realm showed "could not be counted" and could never be removed. Both are
   live today; this branch fixes them.

Tests: `python3 -I tests/test_gm_powers_unit.py`, `python3 -I tests/test_operations_unit.py`,
`python3 -I tests/test_realm_provision_unit.py`, `python3 -I tests/test_server_control_unit.py`,
`node tests/test_gm_powers_ui.cjs`, `node tests/test_operations_ui.cjs`; server
`~/.dotnet/dotnet run --project server/tests/Godswar.Server.ProtocolChecks -c Release` (new check "GM power policy").
Rig evidence: `docs/evidence/w5b-servers-gm/README.md`.
