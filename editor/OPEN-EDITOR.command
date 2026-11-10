#!/bin/bash
# The permanent HTTPS editor does not need an SSH tunnel.
exec /usr/bin/open "${GODSWAR_EDITOR_URL:-http://127.0.0.1:8080}"
