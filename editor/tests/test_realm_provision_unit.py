"""Unit checks for ops/server-control/godswar251-realm (the root script that sets up / takes down the game process of an
additional realm) and the helper v3 actions that start it. No systemd, no ufw, no root, no VPS: systemctl, ufw and ss
are stubs that record their arguments, /proc and /etc are temp folders, and /readyz is a local test server.
Run: python3 -I tests/test_realm_provision_unit.py"""
import warnings; warnings.simplefilter("ignore", ResourceWarning)
import http.server, importlib.machinery, importlib.util, json, os, socket, subprocess, sys, tempfile, threading, time, unittest

HERE = os.path.dirname(os.path.abspath(__file__))
OPS = os.path.abspath(os.path.join(HERE, "..", "ops", "server-control"))
SCRIPT = os.path.join(OPS, "godswar251-realm")
CONTROL = os.path.join(OPS, "gweditor-control.py")

SYSTEMCTL = r"""#!/bin/sh
D="%(d)s"
echo "$@" >> "$D/calls"
case "$1" in
  show) f="$D/show/$2.$4"; [ -f "$f" ] && cat "$f"; exit 0;;
  start) case "$2" in godswar251-realm@*) [ -f "$D/fail-start" ] && exit 1; echo active > "$D/show/$2.ActiveState"; touch "$D/started";; esac; exit 0;;
  stop) echo inactive > "$D/show/$2.ActiveState"; rm -f "$D/started"; exit 0;;
  disable) echo inactive > "$D/show/$3.ActiveState"; rm -f "$D/started"; exit 0;;
  *) exit 0;;
esac
"""
UFW = '#!/bin/sh\nD="%(d)s"\necho "$@" >> "$D/ufw-calls"\n[ "$1" = status ] && cat "$D/ufw-status"\nexit 0\n'
# ss -Htln sport = :P  -> listening?   ss -Htn state established sport = :P -> connections
SS = r"""#!/bin/sh
D="%(d)s"
for a in "$@"; do p="$a"; done
p=${p#:}
case "$1" in
  -Htln) { [ -f "$D/started" ] || [ -f "$D/listen-$p" ]; } && echo "LISTEN 0 512 0.0.0.0:$p 0.0.0.0:*";;
  -Htn) [ -f "$D/est-$p" ] && cat "$D/est-$p";;
esac
exit 0
"""
MAIN_ENV = "GODSWAR_RUNTIME_PROFILE=LocalDevelopment GODSWAR_PARNASSUS=1 GODSWAR_CHARACTER_GM_FLAGS=true"


class Ready(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.end_headers(); self.wfile.write(b"ready")

    def log_message(self, *a):
        pass


class Rig:
    """One temp machine: stubs, fake /proc, fake unit dir."""

    def __init__(self, handoff="postgres", template=True, realm_env=MAIN_ENV, ufw="Status: active\n"):
        self.t = tempfile.TemporaryDirectory(); d = self.d = self.t.name
        for name, text in (("systemctl", SYSTEMCTL), ("ufw", UFW), ("ss", SS)):
            p = os.path.join(d, name); open(p, "w").write(text % {"d": d}); os.chmod(p, 0o755)
        for sub in ("show", "etc", "state", "units", "proc/4242"):
            os.makedirs(os.path.join(d, sub))
        if template:
            open(os.path.join(d, "units", "godswar251-realm@.service"), "w").write("[Unit]\n")
        self.show("godswar251.service", "ActiveState", "active"); self.show("godswar251.service", "MainPID", "4242")
        self.show("godswar251.service", "Environment", MAIN_ENV)
        for r in (2, 3):
            self.show("godswar251-realm@%d.service" % r, "Environment", realm_env)
        env = b"PATH=/usr/bin\0GODSWAR_PARNASSUS=1\0"
        if handoff:
            env += b"GODSWAR_LEGACY_GAME_LOGIN_HANDOFF_STORE=" + handoff.encode() + b"\0"
        open(os.path.join(d, "proc/4242/environ"), "wb").write(env)
        open(os.path.join(d, "ufw-status"), "w").write(ufw)
        self.http = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Ready)
        threading.Thread(target=self.http.serve_forever, daemon=True).start()
        self.env = dict(os.environ, GWR_SYSTEMCTL=os.path.join(d, "systemctl"), GWR_UFW=os.path.join(d, "ufw"), GWR_SS=os.path.join(d, "ss"),
                        GWR_ETC=os.path.join(d, "etc"), GWR_STATE=os.path.join(d, "state"), GWR_UNIT_DIR=os.path.join(d, "units"),
                        GWR_PROC=os.path.join(d, "proc"), GWR_READY_URL="http://127.0.0.1:%d/readyz?m=%%d" % self.http.server_address[1],
                        GWR_READY_TRIES="4", GWR_KNOCK_SECONDS="0")

    def show(self, unit, prop, value):
        open(os.path.join(self.d, "show", "%s.%s" % (unit, prop)), "w").write(value + "\n")

    def run(self, *args, **env):
        p = subprocess.run([sys.executable, "-I", SCRIPT, *args], capture_output=True, text=True, env=dict(self.env, **env), timeout=60)
        return p.returncode, p.stderr

    def calls(self, name="calls"):
        p = os.path.join(self.d, name)
        return open(p).read().splitlines() if os.path.exists(p) else []

    def state(self, realm):
        p = os.path.join(self.d, "state", "realm-%d.json" % realm)
        return json.load(open(p)) if os.path.exists(p) else None

    def envfile(self, realm):
        p = os.path.join(self.d, "etc", "realm-%d.env" % realm)
        return open(p).read() if os.path.exists(p) else None

    def stop_http(self):
        if self.http:
            self.http.shutdown(); self.http.server_close(); self.http = None

    def close(self):
        self.stop_http(); self.t.cleanup()


class Provision(unittest.TestCase):
    def setUp(self):
        self.rig = Rig(); self.addCleanup(self.rig.close)

    def test_provision_does_everything_in_order_and_only_fixed_things(self):
        code, err = self.rig.run("job", "provision-2-7001")
        self.assertEqual(code, 0, err)
        st = self.rig.state(2)
        self.assertEqual(st["state"], "succeeded"); self.assertEqual(st["port"], 7001); self.assertEqual(st["unit"], "godswar251-realm@2.service")
        self.assertEqual([s["step"] for s in st["steps"]], ["installed", "port free", "shared login handoff", "settings", "feature switches",
                                                            "ufw", "reachable from outside", "started"])
        self.assertTrue(all(s["ok"] for s in st["steps"])); self.assertEqual(st["outside"]["result"], "skipped")
        env = self.rig.envfile(2)
        for line in ("GODSWAR_WORLD_INSTANCE_REALM_ID=2", "GODSWAR_GAME_PORT=7001", "GODSWAR_GAME_PUBLIC_PORT=7001", "GODSWAR_LOGIN_PORT=17002",
                     "GODSWAR_LOGIN_BIND_HOST=127.0.0.1", "GODSWAR_MANAGEMENT_PORT=19002", "GODSWAR_LEGACY_GAME_LOGIN_HANDOFF_STORE=postgres",
                     "GODSWAR_WORLD_INSTANCE_SERVER_NODE_ID=realm-2", "GODSWAR_SECURE_ENABLED=false"):
            self.assertIn(line, env.splitlines())
        self.assertNotIn("assword", env)                                           # no secret is ever written
        self.assertEqual(self.rig.calls("ufw-calls"), ["status", "allow 7001/tcp comment godswar realm 2"])
        changing = [c for c in self.rig.calls() if not c.startswith("show ")]
        self.assertEqual(changing, ["enable godswar251-realm@2.service", "start godswar251-realm@2.service"])   # the main game is never touched

    def test_bad_jobs_change_nothing(self):
        for job in ("provision-1-7001", "provision-2-7000", "provision-2-7100", "provision-256-7001", "provision-2-6999", "provision-2-22",
                    "provision-2-7001;reboot", "remove-1", "remove-0", "remove-2 x", "provision--2-7001", "start-2", "../../x", "provision-02-70011"):
            code, err = self.rig.run("job", job)
            self.assertNotEqual(code, 0, job); self.assertEqual(self.rig.calls("ufw-calls"), [], job)
            self.assertEqual([c for c in self.rig.calls() if not c.startswith("show ")], [], job)
            self.assertIsNone(self.rig.envfile(2), job)

    def test_needs_restart_writes_the_dropin_once_and_stops(self):
        rig = Rig(handoff=None); self.addCleanup(rig.close)
        code, err = rig.run("job", "provision-2-7001")
        self.assertEqual(code, 1); st = rig.state(2)
        self.assertEqual(st["code"], "needs_restart"); self.assertIn("restarted once", st["error"])
        dropin = os.path.join(rig.d, "units", "godswar251.service.d", "realm-handoff.conf")
        self.assertIn("Environment=GODSWAR_LEGACY_GAME_LOGIN_HANDOFF_STORE=postgres", open(dropin).read())
        self.assertEqual([c for c in rig.calls() if not c.startswith("show ")], ["daemon-reload"])     # no restart, no start
        self.assertEqual(rig.calls("ufw-calls"), []); self.assertIsNone(rig.envfile(2))
        before = os.stat(dropin).st_mtime_ns
        code, _ = rig.run("job", "provision-2-7001")                                # again before the restart: nothing more
        self.assertEqual(code, 1); self.assertEqual(os.stat(dropin).st_mtime_ns, before)
        self.assertEqual([c for c in rig.calls() if not c.startswith("show ")], ["daemon-reload"])

    def test_memory_store_spelled_out_also_needs_restart(self):
        rig = Rig(handoff="memory"); self.addCleanup(rig.close)
        self.assertEqual(rig.run("job", "provision-2-7001")[0], 1); self.assertEqual(rig.state(2)["code"], "needs_restart")

    def test_refused_without_template_or_main(self):
        rig = Rig(template=False); self.addCleanup(rig.close)
        self.assertEqual(rig.run("job", "provision-2-7001")[0], 1); self.assertEqual(rig.state(2)["code"], "not_installed")
        self.rig.show("godswar251.service", "ActiveState", "inactive")
        self.assertEqual(self.rig.run("job", "provision-2-7001")[0], 1); self.assertEqual(self.rig.state(2)["code"], "main_down")
        self.assertEqual(self.rig.calls("ufw-calls"), [])

    def test_feature_switch_mismatch_fails_closed(self):
        rig = Rig(realm_env="GODSWAR_RUNTIME_PROFILE=LocalDevelopment"); self.addCleanup(rig.close)   # the drop-in link is missing
        self.assertEqual(rig.run("job", "provision-2-7001")[0], 1); st = rig.state(2)
        self.assertEqual(st["code"], "not_installed"); self.assertIn("GODSWAR_CHARACTER_GM_FLAGS", st["error"])
        self.assertEqual(rig.calls("ufw-calls"), [])                                # before the firewall is touched
        self.assertNotIn("start godswar251-realm@2.service", rig.calls())

    def test_port_in_use_or_owned_by_another_realm(self):
        open(os.path.join(self.rig.d, "listen-7002"), "w").write("")
        self.assertEqual(self.rig.run("job", "provision-2-7002")[0], 1); self.assertEqual(self.rig.state(2)["code"], "port_taken")
        self.assertEqual(self.rig.run("job", "provision-2-7001")[0], 0)
        os.remove(os.path.join(self.rig.d, "started"))                              # realm 2 keeps 7001 in its settings file
        self.assertEqual(self.rig.run("job", "provision-3-7001")[0], 1); self.assertIn("belongs to realm 2", self.rig.state(3)["error"])

    def test_rerun_refused_with_players_and_allowed_when_idle(self):
        self.assertEqual(self.rig.run("job", "provision-2-7001")[0], 0)
        open(os.path.join(self.rig.d, "est-7001"), "w").write("ESTAB 0 0 1.2.3.4:7001 5.6.7.8:50000\n")
        self.assertEqual(self.rig.run("job", "provision-2-7001")[0], 1); self.assertEqual(self.rig.state(2)["code"], "players_online")
        self.assertNotIn("stop godswar251-realm@2.service", self.rig.calls())
        os.remove(os.path.join(self.rig.d, "est-7001"))
        self.assertEqual(self.rig.run("job", "provision-2-7003")[0], 0)             # idle: moved to another port
        self.assertIn("stop godswar251-realm@2.service", self.rig.calls())
        self.assertEqual(self.rig.calls("ufw-calls")[-2:], ["delete allow 7001/tcp", "allow 7003/tcp comment godswar realm 2"])
        self.assertIn("GODSWAR_GAME_PORT=7003", self.rig.envfile(2))

    def test_not_ready_stops_the_process_again(self):
        self.rig.stop_http()                                                        # /readyz never answers
        self.assertEqual(self.rig.run("job", "provision-2-7001")[0], 1); st = self.rig.state(2)
        self.assertEqual(st["code"], "not_ready"); self.assertEqual(self.rig.calls()[-1], "disable --now godswar251-realm@2.service")
        self.assertEqual(self.rig.calls("ufw-calls")[-1], "delete allow 7001/tcp"); self.assertEqual(st["ufw"], "closed")   # nothing half set up
        self.assertIsNone(self.rig.envfile(2)); self.assertEqual(len(os.listdir(os.path.join(self.rig.d, "state", "removed"))), 1)

    def test_ufw_inactive_is_said_not_pretended(self):
        rig = Rig(ufw="Status: inactive\n"); self.addCleanup(rig.close)
        self.assertEqual(rig.run("job", "provision-2-7001")[0], 0)
        self.assertEqual(rig.state(2)["ufw"], "inactive"); self.assertEqual(rig.calls("ufw-calls"), ["status"])


class Knock(unittest.TestCase):
    PORT = 7093

    def test_outside_connection_is_seen_and_silence_is_reported(self):
        try:
            with socket.socket() as s:
                s.bind(("127.0.0.1", self.PORT))
        except OSError:
            self.skipTest("port %d is busy on this machine" % self.PORT)
        rig = Rig(); self.addCleanup(rig.close)

        def knocker():
            for _ in range(60):
                try:
                    socket.create_connection(("127.0.0.1", self.PORT), timeout=0.2).close(); return
                except OSError:
                    time.sleep(0.1)
        threading.Thread(target=knocker, daemon=True).start()
        code, err = rig.run("job", "provision-2-%d" % self.PORT, GWR_KNOCK_SECONDS="6", GWR_KNOCK_BIND="127.0.0.1", GWR_KNOCK_ANY="1")
        self.assertEqual(code, 0, err); self.assertEqual(rig.state(2)["outside"]["result"], "seen")
        # a loopback connection is NOT "outside" without the test switch, and silence is "not_seen" (the job still finishes)
        rig2 = Rig(); self.addCleanup(rig2.close)
        threading.Thread(target=knocker, daemon=True).start()
        code, err = rig2.run("job", "provision-2-%d" % self.PORT, GWR_KNOCK_SECONDS="1.5", GWR_KNOCK_BIND="127.0.0.1")
        self.assertEqual(code, 0, err); st = rig2.state(2)
        self.assertEqual(st["outside"]["result"], "not_seen"); self.assertEqual(st["state"], "succeeded")
        step = [s for s in st["steps"] if s["step"] == "reachable from outside"][0]
        self.assertFalse(step["ok"]); self.assertIn("Hosting by Design", step["detail"])


class Remove(unittest.TestCase):
    def setUp(self):
        self.rig = Rig(); self.addCleanup(self.rig.close)
        self.assertEqual(self.rig.run("job", "provision-2-7001")[0], 0)

    def test_remove_stops_closes_and_keeps_the_settings_copy(self):
        code, err = self.rig.run("job", "remove-2")
        self.assertEqual(code, 0, err); st = self.rig.state(2)
        self.assertEqual(st["state"], "removed"); self.assertEqual(st["port"], 7001)
        self.assertEqual(self.rig.calls()[-1], "disable --now godswar251-realm@2.service")
        self.assertEqual(self.rig.calls("ufw-calls")[-1], "delete allow 7001/tcp")          # the port comes from OUR settings file
        self.assertIsNone(self.rig.envfile(2))
        self.assertEqual(len(os.listdir(os.path.join(self.rig.d, "state", "removed"))), 1)   # moved, not deleted
        self.assertEqual(self.rig.run("job", "remove-2")[0], 0)                              # again: nothing left, still fine

    def test_remove_refused_while_someone_is_connected(self):
        open(os.path.join(self.rig.d, "est-7001"), "w").write("ESTAB 0 0 1.2.3.4:7001 5.6.7.8:50000\n")
        n = len(self.rig.calls("ufw-calls"))
        self.assertEqual(self.rig.run("job", "remove-2")[0], 1); self.assertEqual(self.rig.state(2)["code"], "players_online")
        self.assertNotIn("disable --now godswar251-realm@2.service", self.rig.calls())
        self.assertEqual(len(self.rig.calls("ufw-calls")), n); self.assertIsNotNone(self.rig.envfile(2))

    def test_tampered_settings_file_cannot_point_ufw_elsewhere(self):
        open(os.path.join(self.rig.d, "etc", "realm-2.env"), "w").write("GODSWAR_GAME_PORT=22\n")
        self.assertEqual(self.rig.run("job", "remove-2")[0], 0)
        self.assertNotIn("delete allow 22/tcp", self.rig.calls("ufw-calls"))

    def test_status_lists_what_is_set_up(self):
        p = subprocess.run([sys.executable, "-I", SCRIPT, "status"], capture_output=True, text=True, env=self.rig.env, timeout=30)
        self.assertEqual(json.loads(p.stdout)["realms"], [{"realm": 2, "port": 7001, "unit": "godswar251-realm@2.service", "active": "active",
                                                           "listening": True, "connections": 0}])


class HelperV3(unittest.TestCase):
    """gweditor-control: the two realm actions start only the fixed oneshot unit, with two validated numbers."""

    def run_helper(self, request, state=None, fail=False):
        with tempfile.TemporaryDirectory() as d:
            stub = os.path.join(d, "systemctl")
            open(stub, "w").write("#!/bin/sh\necho \"$@\" >> %s/calls\nexit %d\n" % (d, 1 if fail else 0)); os.chmod(stub, 0o755)
            os.mkdir(os.path.join(d, "r")); os.mkdir(os.path.join(d, "realms"))
            if state:
                json.dump(state, open(os.path.join(d, "realms", "realm-%d.json" % state["realm"]), "w"))
            env = dict(os.environ, GWC_ROOT=os.path.join(d, "r"), GWC_SYSTEMCTL=stub, GWC_ROOT_UID=str(os.getuid()), GWC_REALM_STATE=os.path.join(d, "realms"))
            p = subprocess.run([sys.executable, "-I", CONTROL], input=(json.dumps(request) + "\n").encode(), capture_output=True, env=env, timeout=30)
            reply = json.loads(p.stdout.splitlines()[0])
            job = os.path.join(d, "r", str(request.get("job")) + ".json")
            doc = json.load(open(job)) if os.path.exists(job) else None
            calls = open(os.path.join(d, "calls")).read() if os.path.exists(os.path.join(d, "calls")) else ""
            return reply, doc, calls

    def test_version_and_actions(self):
        spec = importlib.util.spec_from_file_location("gwc3", CONTROL)
        mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)
        self.assertEqual(mod.VERSION, 3)
        self.assertEqual(mod.ACTIONS, {"restart", "start", "stop", "backup", "logs", "backups", "restore", "realm_provision", "realm_remove"})

    def test_provision_starts_only_the_fixed_unit_and_copies_the_record(self):
        state = {"realm": 2, "state": "succeeded", "steps": [{"step": "started", "ok": True, "detail": ""}], "outside": {"result": "seen"}}
        reply, doc, calls = self.run_helper({"action": "realm_provision", "job": "a" * 32, "realm": 2, "port": 7001}, state)
        self.assertEqual(reply["unit"], "godswar251-realm-admin@provision-2-7001.service")
        self.assertEqual(calls.strip(), "start godswar251-realm-admin@provision-2-7001.service")
        self.assertEqual(doc["state"], "succeeded"); self.assertEqual(doc["realm"]["outside"]["result"], "seen"); self.assertEqual(doc["helper_version"], 3)

    def test_remove_starts_only_the_fixed_unit(self):
        reply, doc, calls = self.run_helper({"action": "realm_remove", "job": "b" * 32, "realm": 2}, {"realm": 2, "state": "removed", "steps": []})
        self.assertEqual(calls.strip(), "start godswar251-realm-admin@remove-2.service"); self.assertEqual(doc["state"], "succeeded")

    def test_failure_carries_the_scripts_own_sentence(self):
        state = {"realm": 2, "state": "failed", "code": "needs_restart", "error": "The main game server must be restarted once", "steps": []}
        _, doc, _ = self.run_helper({"action": "realm_provision", "job": "c" * 32, "realm": 2, "port": 7001}, state, fail=True)
        self.assertEqual(doc["state"], "failed"); self.assertIn("restarted once", doc["error"]); self.assertEqual(doc["realm"]["code"], "needs_restart")

    def test_anything_but_two_small_numbers_is_refused_before_any_call(self):
        bad = [{"realm": 1, "port": 7001}, {"realm": 256, "port": 7001}, {"realm": 2, "port": 7000}, {"realm": 2, "port": 7100}, {"realm": 2, "port": 22},
               {"realm": "2", "port": 7001}, {"realm": 2, "port": "7001"}, {"realm": True, "port": 7001}, {"realm": 2.0, "port": 7001},
               {"realm": "2-7001.service; reboot", "port": 7001}, {"realm": 2}, {"port": 7001}, {"realm": None, "port": None},
               {"realm": 2, "port": 7001.5}, {"realm": [2], "port": 7001}]
        for extra in bad:
            reply, doc, calls = self.run_helper(dict({"action": "realm_provision", "job": "d" * 32}, **extra))
            self.assertIn("error", reply, extra); self.assertIsNone(doc, extra); self.assertEqual(calls, "", extra)
        for extra in ({"realm": 1}, {"realm": "2"}, {"realm": 300}, {}, {"realm": "../x"}):
            reply, doc, calls = self.run_helper(dict({"action": "realm_remove", "job": "e" * 32}, **extra))
            self.assertIn("error", reply, extra); self.assertEqual(calls, "", extra)


class UnitFiles(unittest.TestCase):
    def test_template_and_admin_units(self):
        t = open(os.path.join(OPS, "godswar251-realm@.service")).read()
        for line in ("PartOf=godswar251.service", "EnvironmentFile=/etc/godswar251-realms/realm-%i.env", "User=godswar251",
                     "ExecStart=/opt/godswar251/server/Godswar.Server /opt/godswar251/appsettings.json",
                     "StandardOutput=append:/opt/godswar251/logs/realm-%i.log", "ExecStartPre=/usr/local/sbin/godswar251-realm wait-main", "WantedBy=godswar251.service", "After=network-online.target postgresql.service godswar251.service"):
            self.assertIn(line, t.splitlines())
        a = open(os.path.join(OPS, "godswar251-realm-admin@.service")).read()
        self.assertIn("ExecStart=/usr/local/sbin/godswar251-realm job %i", a.splitlines()); self.assertIn("Type=oneshot", a.splitlines())

    def test_script_ranges_match_the_helper(self):
        loader = importlib.machinery.SourceFileLoader("gwrealm", SCRIPT)
        spec = importlib.util.spec_from_loader("gwrealm", loader); r = importlib.util.module_from_spec(spec); loader.exec_module(r)
        spec = importlib.util.spec_from_file_location("gwc3b", CONTROL); c = importlib.util.module_from_spec(spec); spec.loader.exec_module(c)
        self.assertEqual((r.REALM_MIN, r.REALM_MAX, r.PORT_MIN, r.PORT_MAX), (c.REALM_MIN, c.REALM_MAX, c.REALM_PORT_MIN, c.REALM_PORT_MAX))
        for realm, port in ((2, 7001), (255, 7099)):
            self.assertTrue(r.JOB.fullmatch("provision-%d-%d" % (realm, port)))
            self.assertEqual(c.realm_unit("realm_provision", {"realm": realm, "port": port}), "godswar251-realm-admin@provision-%d-%d.service" % (realm, port))


if __name__ == "__main__":
    unittest.main(verbosity=2)
