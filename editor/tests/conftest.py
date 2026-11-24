"""pytest set-up for the editor tests, so `python3 -m pytest editor/tests` runs all of them (EDITOR-SMOKE, 2026-11-22).

Two kinds of test files live here:
  * unittest / pytest modules (classes or test_ functions): collected by pytest as usual.
  * self-checking scripts (print "ok"/"FAIL" lines and sys.exit at import). Imported by pytest they stopped the whole
    run (SystemExit = INTERNALERROR). Each one is now one test item that runs `python3 -I <file>` in a subprocess:
    exit 0 = pass. A script that needs arguments (the rig and live-server tests: --login, --db, a probe path, ...)
    exits 2 with argparse's usage text and is SKIPPED with that reason; run those by hand as their docstring says.

server.py imports ../gwadmin.py, which is not in the repo (it is GodsWar-VPS/tools/gwadmin.py, the same file as the VPS
copy). The folder is found by editor/tools/smoke.py find_gwadmin_dir() ($GWADMIN_TOOLS, then GodsWar-VPS/tools above
the checkout) and put on sys.path.
"""
import re
import subprocess
import sys
from pathlib import Path

import pytest

TESTS = Path(__file__).resolve().parent
EDITOR = TESTS.parent
sys.path.insert(0, str(EDITOR / "tools"))
from smoke import find_gwadmin_dir  # noqa: E402

_GW = find_gwadmin_dir()
if _GW and _GW not in sys.path:
    sys.path.insert(0, _GW)
if str(EDITOR) not in sys.path:
    sys.path.insert(0, str(EDITOR))

SCRIPT_TIMEOUT = 600
_TESTLIKE = re.compile(r"^\s*(class \w+\(.*TestCase\)|def test_)", re.M)


def _is_script(path):
    return path.name.startswith("test_") and path.suffix == ".py" and not _TESTLIKE.search(path.read_text(errors="replace"))


def pytest_pycollect_makemodule(module_path, parent):
    if _is_script(Path(module_path)):
        return ScriptFile.from_parent(parent, path=Path(module_path))
    return None


class ScriptFile(pytest.File):
    def collect(self):
        yield ScriptItem.from_parent(self, name=self.path.stem)


class ScriptFailed(Exception):
    pass


class ScriptItem(pytest.Item):
    def runtest(self):
        p = subprocess.run([sys.executable, "-I", str(self.path)], cwd=str(EDITOR), capture_output=True, text=True,
                           timeout=SCRIPT_TIMEOUT)
        out = (p.stdout or "") + (p.stderr or "")
        if p.returncode == 2 and "usage:" in out:
            usage = next((l for l in out.splitlines() if "required" in l or l.startswith("usage:")), "needs arguments")
            pytest.skip("rig/live test, needs arguments: " + usage.strip()[:160])
        if p.returncode != 0:
            raise ScriptFailed("exit %d\n%s" % (p.returncode, "\n".join(out.splitlines()[-40:])))

    def repr_failure(self, excinfo):
        if isinstance(excinfo.value, ScriptFailed):
            return "%s failed: %s" % (self.path.name, excinfo.value)
        return super().repr_failure(excinfo)

    def reportinfo(self):
        return self.path, 0, "script: %s" % self.path.name
