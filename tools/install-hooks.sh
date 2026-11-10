#!/usr/bin/env bash
# Install a git pre-commit hook that runs the leak scan, so personal details cannot be committed.
set -euo pipefail
cd "$(dirname "$0")/.."
printf '#!/bin/sh\nexec python3 -I tools/leak-scan.py\n' > .git/hooks/pre-commit
chmod +x .git/hooks/pre-commit
echo "pre-commit leak scan installed."
