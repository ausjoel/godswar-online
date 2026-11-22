#!/usr/bin/env python3
"""gweditor-control v3: the editor's narrow root helper. Socket-activated (gweditor-control.socket, one request per
connection, JSON on one line). It never accepts shell text, unit names or paths:

  start | stop | restart   systemctl <action> godswar251.service (+ wait for /readyz after start/restart)
  backup                   systemctl start godswar251-backup.service (the nightly dump unit, 14-day retention)
  logs   {lines}           the last 10..300 lines of /opt/godswar251/logs/server.log, secrets redacted
  backups                  list the dumps in /opt/godswar251/backup{,/nightly,/pre-restore} (name, size, time)
  restore {file}           systemctl start godswar251-restore@<file>.service; <file> must be the BASENAME of a listed
                           dump. The restore unit (godswar251-restore) refuses with players online, takes a
                           pre-restore dump, stops the game, restores, starts it, and puts the old data back on failure.

  realm_provision {realm, port}   v3: systemctl start godswar251-realm-admin@provision-<realm>-<port>.service
  realm_remove {realm}            v3: systemctl start godswar251-realm-admin@remove-<realm>.service
                           realm is a number 2..255 and port 7001..7099; the unit runs /usr/local/sbin/godswar251-realm,
                           which derives every name, path and other port from those two numbers, re-validates them, and
                           writes what it did to /var/lib/godswar251-realms/realm-<realm>.json (copied into the job record).
                           It never touches the database and never restarts the main game.

The editor decides who may ask (an editor_admin sign-in) and enforces the player-count acknowledgement and the typed
confirmation; this helper re-validates every argument. v1 (Development/server-control/control.py) had the first five.
"""
import datetime, fcntl, json, os, pathlib, re, subprocess, sys, time, urllib.request

# GWC_* variables exist only so the tests can point the helper at a stub; the systemd unit sets none of them.
ROOT = pathlib.Path(os.environ.get('GWC_ROOT', '/run/gweditor-control'))
SYSTEMCTL = os.environ.get('GWC_SYSTEMCTL', '/usr/bin/systemctl')
LOGFILE = os.environ.get('GWC_LOGFILE', '/opt/godswar251/logs/server.log')
READYZ = os.environ.get('GWC_READYZ', 'http://127.0.0.1:9090/readyz')
BACKUP = pathlib.Path(os.environ.get('GWC_BACKUP', '/opt/godswar251/backup'))
BACKUP_DIRS = ('', 'nightly', 'pre-restore')
READY_TRIES = int(os.environ.get('GWC_READY_TRIES', '90'))     # x 0.5 s
VERSION = 3
ACTIONS = {'restart', 'start', 'stop', 'backup', 'logs', 'backups', 'restore', 'realm_provision', 'realm_remove'}
REALM_STATE = pathlib.Path(os.environ.get('GWC_REALM_STATE', '/var/lib/godswar251-realms'))
REALM_MIN, REALM_MAX, REALM_PORT_MIN, REALM_PORT_MAX = 2, 255, 7001, 7099
SERVICE_ACTIONS = {'restart', 'start', 'stop'}
DUMP = re.compile(r'godswar251-(?:pre-restore-)?\d{4}-\d{2}-\d{2}-\d{6}\.sql\.gz')
LOG_MAX_LINES = 300; LOG_MAX_LINE = 300; OUT_MAX = 40000
SECRET = [(re.compile(r'(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+'), 'Bearer [hidden]'),
          (re.compile(r'(?i)(password|passwd|pwd|secret|token|api[_-]?key|apikey|authorization|bearer|connectionstring)(["\']?\s*[=:]\s*|\s+)("[^"]*"|\'[^\']*\'|[^\s;,&]+)'), r'\1\2[hidden]'),
          (re.compile(r'(?i)\b(postgres(?:ql)?|mysql|mariadb|redis|amqp|https?)://([^/\s:@]+):([^@\s]+)@'), r'\1://\2:[hidden]@'),
          (re.compile(r'\b[A-Fa-f0-9]{32,}\b'), '[hidden]'),
          (re.compile(r'\b[A-Za-z0-9_\-]{40,}\b'), '[hidden]')]


def redact(text):
    for rx, sub in SECRET:
        text = rx.sub(sub, text)
    return text


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def write(job, doc):
    p = ROOT / (job + '.json'); temp = ROOT / (job + '.tmp')
    temp.write_text(json.dumps(doc)); temp.chmod(0o640); os.replace(temp, p)


def reply(doc):
    sys.stdout.write(json.dumps(doc) + '\n'); sys.stdout.flush()


def tail_log(lines):
    try:
        n = int(lines)
    except (TypeError, ValueError):
        n = 100
    n = max(10, min(LOG_MAX_LINES, n))
    with open(LOGFILE, 'rb') as f:
        f.seek(0, os.SEEK_END); size = f.tell(); f.seek(max(0, size - n * 400))
        raw = f.read().decode('utf-8', 'replace')
    rows = [redact(l)[:LOG_MAX_LINE] for l in raw.splitlines()[-n:]]
    return '\n'.join(rows)[-OUT_MAX:]


def wait_ready():
    for _ in range(READY_TRIES):
        try:
            with urllib.request.urlopen(READYZ, timeout=1) as response:
                if response.status == 200:
                    return True
        except Exception:
            pass
        time.sleep(.5)
    return False


def systemctl(*args, timeout=60):
    result = subprocess.run([SYSTEMCTL, *args], capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError(redact((result.stderr or result.stdout).strip())[:500] or 'systemctl failed (%d)' % result.returncode)


def service(action, doc):
    systemctl(action, 'godswar251.service')
    if action != 'stop' and not wait_ready():
        raise RuntimeError('Service started but game readiness did not pass within 45 seconds')


def list_backups():
    out = []
    for sub in BACKUP_DIRS:
        d = BACKUP / sub if sub else BACKUP
        try:
            names = os.listdir(d)
        except OSError:
            continue
        for name in names:
            p = d / name
            if DUMP.fullmatch(name) and p.is_file() and not p.is_symlink():
                st = p.stat()
                out.append({'file': name, 'folder': sub or '.', 'bytes': st.st_size,
                            'time': datetime.datetime.fromtimestamp(st.st_mtime, datetime.timezone.utc).isoformat()})
    out.sort(key=lambda r: r['time'], reverse=True)
    return out[:200]


def find_backup(name):
    """The one listed dump called <name> (basename only; symlinks and other names refused)."""
    if not isinstance(name, str) or not DUMP.fullmatch(name):
        raise ValueError('not a backup file name')
    hits = [r for r in list_backups() if r['file'] == name]
    if len(hits) != 1:
        raise ValueError('backup file not found')
    return hits[0]


def whole(value, lo, hi, what):
    """A JSON integer in lo..hi (not a string, not a bool, not a float)."""
    if isinstance(value, bool) or not isinstance(value, int) or not lo <= value <= hi:
        raise ValueError('%s must be a whole number %d..%d' % (what, lo, hi))
    return value


def realm_unit(action, d):
    """The fixed oneshot unit of a realm job; raises ValueError for anything but the two numbers."""
    realm = whole(d.get('realm'), REALM_MIN, REALM_MAX, 'realm')
    if action == 'realm_remove':
        return 'godswar251-realm-admin@remove-%d.service' % realm
    return 'godswar251-realm-admin@provision-%d-%d.service' % (realm, whole(d.get('port'), REALM_PORT_MIN, REALM_PORT_MAX, 'port'))


def realm_state(realm):
    """What godswar251-realm wrote about its last run for this realm (steps, outside test), or None."""
    p = REALM_STATE / ('realm-%d.json' % realm)
    try:
        if p.is_symlink() or p.stat().st_size > 65536:
            return None
        doc = json.loads(p.read_text())
    except (OSError, ValueError):
        return None
    return doc if isinstance(doc, dict) else None


def main():
    raw = sys.stdin.buffer.readline(1025)
    if len(raw) > 1024:
        reply({'error': 'request too large'}); return
    try:
        d = json.loads(raw)
    except Exception:
        reply({'error': 'invalid JSON'}); return
    action = d.get('action'); job = d.get('job')
    if action not in ACTIONS or not isinstance(job, str) or not re.fullmatch('[a-f0-9]{32}', job):
        reply({'error': 'invalid action or job'}); return
    if action == 'restore':
        try:
            find_backup(d.get('file'))
        except (ValueError, OSError) as e:
            reply({'error': str(e)}); return
    realm_job = None
    if action in ('realm_provision', 'realm_remove'):
        try:
            realm_job = realm_unit(action, d)
        except ValueError as e:
            reply({'error': str(e)}); return
    # /run parent is root-owned; the web editor has read-only access to ROOT.
    if not ROOT.is_dir() or ROOT.is_symlink() or ROOT.stat().st_uid != int(os.environ.get('GWC_ROOT_UID', '0')):
        reply({'error': 'invalid state directory'}); return
    lock = (ROOT / 'lock').open('a')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        reply({'error': 'another server operation is running'}); return
    unit = {'backup': 'godswar251-backup.service', 'logs': 'server log', 'backups': 'backup folder'}.get(action, 'godswar251.service')
    if action == 'restore':
        unit = 'godswar251-restore@%s.service' % d['file']
    if realm_job:
        unit = realm_job
    doc = {'job': job, 'action': action, 'state': 'running', 'started_at': now(), 'unit': unit, 'helper_version': VERSION}
    if action == 'restore':
        doc['file'] = d['file']
    if realm_job:
        doc['realm_id'] = d['realm']
        if action == 'realm_provision':
            doc['port'] = d['port']
    write(job, doc); reply(doc)
    try:
        if action == 'logs':
            doc['output'] = tail_log(d.get('lines'))
        elif action == 'backups':
            doc['backups'] = list_backups()
            doc['output'] = '%d backup files' % len(doc['backups'])
        elif action == 'backup':
            # The same fixed oneshot unit the nightly timer runs; it keeps its own 14-day retention.
            systemctl('start', 'godswar251-backup.service', timeout=600)
            doc['output'] = 'Database backup finished (/opt/godswar251/backup/nightly/latest.sql.gz).'
        elif action == 'restore':
            # A oneshot: start returns when the restore script has finished (or failed and put the old data back).
            systemctl('start', unit, timeout=3600)
            doc['output'] = 'Restored %s; the game server was started again.' % d['file']
            if not wait_ready():
                raise RuntimeError('Restore finished but game readiness did not pass within 45 seconds')
        elif realm_job:
            # A oneshot: start returns when godswar251-realm has finished. Its own record says what happened.
            try:
                systemctl('start', unit, timeout=480)
            finally:
                doc['realm'] = realm_state(d['realm'])
            done = (doc['realm'] or {}).get('state')
            if done not in ('succeeded', 'removed'):
                raise RuntimeError('the realm job ended without a result record')
            doc['output'] = 'Realm %d: %s.' % (d['realm'], 'process running' if done == 'succeeded' else 'process stopped, port closed')
        else:
            service(action, doc)
        doc.update(state='succeeded', finished_at=now())
    except Exception as error:
        message = redact(str(error))[:500]
        if realm_job and isinstance(doc.get('realm'), dict) and doc['realm'].get('error'):
            message = str(doc['realm']['error'])[:500]          # the script's own sentence, not "systemctl failed"
        doc.update(state='failed', error=message, finished_at=now())
    write(job, doc)
    files = sorted((p for p in ROOT.glob('*.json') if re.fullmatch(r'[a-f0-9]{32}\.json', p.name)), key=lambda p: p.stat().st_mtime, reverse=True)
    for p in files[30:]:
        p.unlink()


if __name__ == '__main__':
    main()
