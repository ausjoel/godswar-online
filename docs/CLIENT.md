# Getting a client

GodsWar's game client, art and music belong to their owners and are not distributed here.
Use a legitimately obtained copy of the game. This server speaks the **GodsWar 2.51** protocol.

**Important, and not finished:** the stock 2.51 USA client crashes on some of this server's
content (for example a missing NPC model). The client the server is tested with is the
"Origin" 2.51 client (the 2.51.002 install plus extra data files) with a set of patches
(English text, quest and UI fixes, larger limits). Those patch scripts currently depend on the
maintainer's private folder layout and are **not packaged in this repository yet**. Until they
are, expect to use the stock client only to reach the character screen, and ask in the project
issues for the patch status.

## Pointing it at your server
In the client folder, edit `config.ini`:

```ini
[SERVER]
IP=YOUR_SERVER_IP_OR_DOMAIN
PORT=6999
```
`IP` and `PORT` are the address and login port `setup.sh` printed at the end.
Log in with the administrator account setup created, or create players with the
sign-up website ([WEBSITE.md](WEBSITE.md)).

## Mac and Linux
The client is a 32-bit Windows program. Run it with Wine (CrossOver, Sikarugir or a
Wine build with DXVK for Direct3D 9).

## Distributing to your players
`launcher/` builds a small launcher that downloads your client from a web server you
control and applies updates: see [launcher/README.md](../launcher/README.md). You must
create your own signing key first: `tools/make-update-key.sh`.
