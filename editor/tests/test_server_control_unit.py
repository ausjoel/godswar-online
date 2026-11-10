"""Unit checks for the service-control additions: secret redaction (editor + helper), the log tail cap, the helper's
fixed action list. No network, no server, no systemd: the helper runs against a stub systemctl and a temp log.
Run: python3 -I tests/test_server_control_unit.py"""
import importlib.util, json, os, subprocess, sys, tempfile, unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))
CONTROL = os.path.join(TOOLS, "Development", "server-control", "control.py")
sys.path.insert(0, os.path.join(HERE, ".."))
from modules import world  # noqa: E402

SAMPLES = ["Host=127.0.0.1;Username=godswar;Password=SuperSecret123;Port=5432", "postgres://godswar:hunter2@localhost/db",
           "token=abcdefabcdefabcdefabcdefabcdefabcdefabcd", "Authorization: Bearer abc.def.ghi", "api_key: sk-live-1234567", '"Password": "JsonSecret99"', "'ApiKey': 'QuoteSecret77'"]
LEAKS = ["SuperSecret123", "hunter2", "abcdefabcdef", "abc.def.ghi", "sk-live-1234567", "JsonSecret99", "QuoteSecret77"]


class Redaction(unittest.TestCase):
    def test_editor(self):
        out = "\n".join(world.redact(s) for s in SAMPLES)
        for leak in LEAKS:
            self.assertNotIn(leak, out)
        self.assertIn("Host=127.0.0.1", out)

    def test_helper_loads_and_matches_editor(self):
        spec = importlib.util.spec_from_file_location("gwc", CONTROL)
        mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)
        for s in SAMPLES:
            self.assertEqual(mod.redact(s), world.redact(s))
        self.assertEqual(mod.ACTIONS, {"restart", "start", "stop", "backup", "logs"})


class Helper(unittest.TestCase):
    def run_helper(self, request, log="", env_extra=None):
        with tempfile.TemporaryDirectory() as d:
            stub = os.path.join(d, "systemctl"); open(stub, "w").write("#!/bin/sh\necho \"$@\" >> %s/calls\nexit 0\n" % d); os.chmod(stub, 0o755)
            logf = os.path.join(d, "server.log"); open(logf, "w").write(log)
            os.mkdir(os.path.join(d, "r"))
            env = dict(os.environ, GWC_ROOT=os.path.join(d, "r"), GWC_SYSTEMCTL=stub, GWC_LOGFILE=logf, GWC_ROOT_UID=str(os.getuid()),
                       GWC_READYZ="http://127.0.0.1:1/x")
            env.update(env_extra or {})
            p = subprocess.run([sys.executable, "-I", CONTROL], input=(json.dumps(request) + "\n").encode(), capture_output=True, env=env, timeout=30)
            reply = json.loads(p.stdout.splitlines()[0])
            job = os.path.join(d, "r", request["job"] + ".json")
            doc = json.load(open(job)) if os.path.exists(job) else None
            calls = open(os.path.join(d, "calls")).read() if os.path.exists(os.path.join(d, "calls")) else ""
            return reply, doc, calls

    def test_logs_capped_and_redacted(self):
        log = "\n".join(["row %d" % i for i in range(1000)] + ["Password=SuperSecret123"])
        _, doc, calls = self.run_helper({"action": "logs", "job": "a" * 32, "lines": 99999}, log)
        self.assertEqual(doc["state"], "succeeded"); self.assertLessEqual(len(doc["output"].splitlines()), 300)
        self.assertNotIn("SuperSecret123", doc["output"]); self.assertEqual(calls, "")

    def test_backup_runs_only_the_fixed_unit(self):
        _, doc, calls = self.run_helper({"action": "backup", "job": "b" * 32})
        self.assertEqual(doc["state"], "succeeded"); self.assertEqual(calls.strip(), "start godswar251-backup.service")

    def test_old_jobs_are_pruned_even_for_logs(self):
        with tempfile.TemporaryDirectory() as d:
            logf = os.path.join(d, "server.log"); open(logf, "w").write("x\n"); os.mkdir(os.path.join(d, "r"))
            env = dict(os.environ, GWC_ROOT=os.path.join(d, "r"), GWC_LOGFILE=logf, GWC_ROOT_UID=str(os.getuid()))
            for i in range(34):
                subprocess.run([sys.executable, "-I", CONTROL], input=(json.dumps({"action": "logs", "job": "%032x" % (i + 1)}) + "\n").encode(), capture_output=True, env=env, timeout=30)
            self.assertLessEqual(len([f for f in os.listdir(os.path.join(d, "r")) if f.endswith(".json")]), 30)

    def test_unknown_action_refused(self):
        reply, doc, calls = self.run_helper({"action": "rm -rf", "job": "c" * 32})
        self.assertIn("error", reply); self.assertIsNone(doc); self.assertEqual(calls, "")


if __name__ == "__main__":
    unittest.main(verbosity=2)
