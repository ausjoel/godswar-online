"""gwadmin-web module editors (contract: ../MODULES.md).

Every file modules/<name>.py (not starting with "_", not common.py) is one game-system editor. It registers itself
with common.module("<name>", ...) and its routes with @M.get / @M.post; server.py sends every /api/m/... request to
dispatch() below. A module that fails to import is listed with its error, the others keep working.
"""
import importlib
import os
import traceback

from . import common

HERE = os.path.dirname(os.path.abspath(__file__))
LOAD_ERRORS = {}


def load_all():
    """Import every module file once. Returns {name: error} for the ones that failed."""
    for fn in sorted(os.listdir(HERE)):
        if not fn.endswith(".py") or fn.startswith("_") or fn == "common.py":
            continue
        name = fn[:-3]
        if name in LOAD_ERRORS or ("modules." + name) in importlib.sys.modules:
            continue
        try:
            importlib.import_module("modules." + name)
        except Exception as e:        # noqa: BLE001 - one broken module must not stop the editor
            LOAD_ERRORS[name] = "%s: %s" % (type(e).__name__, e)
            traceback.print_exc()
    return dict(LOAD_ERRORS)


def dispatch(app, method, path, query, body):
    """(status, json-able object) for /api/m/<rest>. query = {key: first value}."""
    return common.dispatch(app, method, path, query, body, LOAD_ERRORS)


def dispatch_undo_live(app, c):
    """(status, json) for /api/undo of a live change (common.undo_live), with the same error mapping as dispatch()."""
    return common.guarded(lambda: {"change": None, **common.undo_live(app, c), "undo_of": c["id"]})
