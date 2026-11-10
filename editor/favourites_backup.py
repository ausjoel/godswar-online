#!/usr/bin/python3
"""Consistent daily snapshots of the service-owned favourite library; retain 14 snapshots."""
import datetime,os,sqlite3
from pathlib import Path
base=Path('/var/lib/gweditor/backups');source=base/'item-favourites.sqlite3'
if not source.exists():raise SystemExit(0)
dest=base/'favourites-daily';dest.mkdir(mode=0o700,exist_ok=True)
path=dest/(datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S')+'.sqlite3')
with sqlite3.connect(source) as src,sqlite3.connect(path) as out:src.backup(out)
os.chmod(path,0o600)
for old in sorted(dest.glob('*.sqlite3'))[:-14]:old.unlink()
print('Favourite-library snapshot saved:',path.name)
