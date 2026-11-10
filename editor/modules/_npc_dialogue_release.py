"""NPC text successor v1: canonical server hash and immutable release validation.
See WorldContentRevisionHasher and PostgresNpcDialogueBaselinePublisher.Successors.cs.
"""
import hashlib
import json
import re
import struct
from copy import deepcopy
from .common import Bad, Refused

REVIEWED = '5C40A7D7FD9C3C6C3FBD8D99F418D4C5F22C741B7E1A5CBE2186F30504E128B9'
SPAWN = '11F3F145343059ACE88D0CFF953AAD7F4FD10988BB253278975EDDE6DB96AF66'
PUBLISHER = 'gwadmin-web-npc-text-v1'
TABLES = {
 'npc_dialogue_texts': ['npc_key','scene_key','display_name','description'],
 'npc_dialogue_profiles': ['profile_key','dialog_index','behavior','initial_request_sub_id'],
 'npc_dialogue_bindings': ['npc_key','client_script_key','profile_key','route_order'],
 'npc_dialogue_profile_entries': ['profile_key','menu_order','sub_id'],
}

def revision(value):
 if not isinstance(value,str) or not re.fullmatch('[0-9A-F]{64}',value):
  raise Bad('A complete displayed revision is required.')
 return value

def ordinal(value): return value.encode('utf-16-be')

def digest(bundle):
 """Length-prefixed UTF-8 and little-endian int32, including ordered multi-routes."""
 def integer(n): return struct.pack('<i',n)
 def string(s):
  b=s.encode('utf-8');return integer(len(b))+b
 def start(family): return integer(1)+string(family)
 texts=sorted(bundle['npc_dialogue_texts'],key=lambda r:ordinal(r['npc_key']))
 profiles={r['profile_key']:r for r in bundle['npc_dialogue_profiles']}
 entries=bundle['npc_dialogue_profile_entries']
 routes=sorted(bundle['npc_dialogue_bindings'],key=lambda r:(ordinal(r['npc_key']),r['route_order']))
 raw=bytearray(start('npc-dialogues')+integer(len(texts)))
 for t in texts:
  raw+=b''.join(string(t[k]) for k in TABLES['npc_dialogue_texts'])
 raw+=integer(len(routes));multi=any(r['route_order']!=0 for r in routes)
 for r in routes:
  p=profiles[r['profile_key']]
  subs=[e['sub_id'] for e in sorted(entries,key=lambda e:e['menu_order']) if e['profile_key']==r['profile_key']]
  raw+=string(r['npc_key'])+string(r['client_script_key'])+integer(p['dialog_index'])+integer(p['behavior'])+integer(len(subs))
  raw+=b''.join(integer(n) for n in subs)
  if multi:raw+=integer(r['route_order'])
 payload=hashlib.sha256(raw).hexdigest().upper()
 manifest=start('npc-dialogue-release')+integer(1)+string(payload)+integer(len(texts)+len(routes))+string(bundle['header']['spawn_revision'])
 return hashlib.sha256(manifest).hexdigest().upper()

def validate(bundle,root):
 if not bundle or not root or not bundle.get('header') or not root.get('header'):
  raise Refused('The reviewed NPC dialogue release is missing.')
 if bundle['header']['spawn_revision']!=SPAWN or root['header']['spawn_revision']!=SPAWN:
  raise Refused('This NPC spawn release needs a newly reviewed editor contract.')
 for table,cols in TABLES.items():
  a=bundle[table];b=root[table]
  fields=cols[:2] if table=='npc_dialogue_texts' else cols
  signature=lambda rows:sorted(tuple(r[k] for k in fields) for r in rows)
  if signature(a)!=signature(b):raise Refused('NPC keys, scenes, routes and menus must match the reviewed release.')
 counts={'text_count':'npc_dialogue_texts','profile_count':'npc_dialogue_profiles','route_count':'npc_dialogue_bindings','menu_entry_count':'npc_dialogue_profile_entries'}
 for name,table in counts.items():
  if bundle['header'][name]!=len(bundle[table]) or bundle['header'][name]!=root['header'][name]:
   raise Refused('The NPC dialogue release is incomplete.')
 if digest(root)!=REVIEWED or digest(bundle)!=bundle['header']['revision']:
  raise Refused('The NPC dialogue content does not match its server hash.')

def depth(value,registrations):
 rows={r['revision']:r for r in registrations};seen=set();n=0
 while value!=REVIEWED:
  if value in seen or n>=64:raise Refused('The NPC dialogue successor chain is cyclic or too long.')
  seen.add(value);r=rows.get(value)
  if not r or r['publisher']!=PUBLISHER:raise Refused('This NPC dialogue revision is not an approved successor.')
  value=r['predecessor_revision'];n+=1
 return n

def edited(bundle,changes):
 if not isinstance(changes,list) or not 1<=len(changes)<=416:raise Bad('Choose at least one NPC greeting to change.')
 result=deepcopy(bundle);bykey={r['npc_key']:r for r in result['npc_dialogue_texts']};seen=set()
 for change in changes:
  if not isinstance(change,dict) or set(change)!={'npc_key','display_name','description'}:
   raise Bad('Only NPC greeting names and descriptions can be edited.')
  key=change['npc_key']
  if not isinstance(key,str) or key not in bykey or key in seen:raise Bad('Choose each published NPC once.')
  seen.add(key)
  for field,limit in [('display_name',1024),('description',16384)]:
   value=change[field]
   if not isinstance(value,str) or not value.strip() or '\x00' in value:raise Bad('NPC text must be nonempty Unicode without NUL.')
   try:size=len(value.encode('utf-8'))
   except UnicodeError:raise Bad('NPC text contains invalid Unicode.')
   if size>limit or (field=='display_name' and len(value)>255):raise Bad('NPC %s exceeds its text limit.'%field)
   bykey[key][field]=value
 return result

def token(plan):
 return hashlib.sha256(json.dumps(plan,sort_keys=True,ensure_ascii=True,separators=(',',':')).encode()).hexdigest()
