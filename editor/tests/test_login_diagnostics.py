import pathlib,sys,tempfile,unittest
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]))
from modules.login_diagnostics import recent_sessions

LINE = "[game-session-end] utc-seconds=1790000000 character-id=10 account-id=44 reason=peer_eof error=none transport-error=none duration-ms=100 last-opcode=10001 last-send-opcode=10090 last-send-bytes=120 character-loaded=True client-ready=False ui-ready=False bootstrap=False"
class SessionDiagnosticsTests(unittest.TestCase):
 def read(self,text,cid=10):
  with tempfile.NamedTemporaryFile() as f:
   f.write(text.encode());f.flush();return recent_sessions(f.name,cid)
 def test_metadata_only_and_character_filter(self):
  rows=self.read(LINE+" password=secret packet=abcdef\n"+LINE.replace("character-id=10","character-id=100"))
  self.assertEqual(len(rows),1);self.assertTrue(rows[0]["ended_before_ready"])
  self.assertNotIn("password",rows[0]);self.assertNotIn("packet",rows[0])
  self.assertEqual(self.read("[chat] player says " + LINE),[])
 def test_reject_unbounded_or_malformed_values(self):
  for text in [LINE.replace("error=none","error=postgres://secret"),LINE.replace("duration-ms=100","duration-ms=-1"),LINE.replace("bootstrap=False","bootstrap=no"),LINE+"x"*1500]:
   self.assertEqual(self.read(text),[])
 def test_recent_limit_order_and_missing_log(self):
  text="\n".join(LINE.replace("duration-ms=100",f"duration-ms={i}") for i in range(30))
  rows=self.read(text);self.assertEqual(len(rows),20);self.assertEqual(rows[0]["duration-ms"],29)
  self.assertEqual(recent_sessions(None,10),[])
  self.assertEqual(recent_sessions("/missing/no-log",10),[])
if __name__ == '__main__':unittest.main()
