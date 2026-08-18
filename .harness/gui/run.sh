#!/usr/bin/env bash
# Launches the harness GUI orchestrator (control panel + monitor + specs/sources manager).
# Binds to 127.0.0.1:8787 by default; override with HARNESS_GUI_HOST / HARNESS_GUI_PORT.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"
exec python3 server.py
