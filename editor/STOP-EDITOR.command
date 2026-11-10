#!/bin/bash
# STOP-EDITOR.command - stops the GodsWar editor on the VPS and its connection to this Mac.
PORT=8788
ssh -o BatchMode=yes -o ConnectTimeout=15 vps "pkill -f '^python3 -I gwadmin-web/server.py .*--port $PORT'" </dev/null
pkill -f "ssh .*-L $PORT:127.0.0.1:$PORT vps"
echo "Editor stopped. You can close this window."
